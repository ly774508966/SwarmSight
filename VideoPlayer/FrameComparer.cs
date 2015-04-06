﻿using System.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using Classes;

namespace Justas.Bees.VideoPlayer
{
    public class FrameComparer
    {
        public static List<long> PerformanceHistory = new List<long>(1000);
        
        public int Threshold = 50;
        public int MostRecentFrameIndex = -1; //Always resumes one frame ahead

        public VideoDecoder Decoder;
        public FrameRenderer Renderer;
        public EventHandler<FrameComparisonArgs> FrameCompared;
        public EventHandler<EventArgs> Stopped; 

        private Frame _previousFrame;
        public bool IsPaused;
        private Thread _bufferMonitor;

        public FrameComparer(VideoDecoder decoder, FrameRenderer renderer)
        {
            Decoder = decoder;
            Renderer = renderer;
        }
        
        public void Start()
        {
            //Should resume to the next frame (0 if at beggining)
            var resumeFrame = MostRecentFrameIndex + 1.0;

            SeekTo(resumeFrame / Decoder.VideoInfo.TotalFrames);

            //Set up the buffer monitor & start monitoring
            _bufferMonitor = new Thread(CompareFramesInBuffer);
            _bufferMonitor.Start();

            //Start decoder & renderer
            Decoder.Start();
            Renderer.Start();
        }

        public void Pause(bool stopSelf = true)
        {
            //stop decoding, comparing, rendering
            Decoder.Stop();
            
            if(stopSelf)
                StopComparing();

            Renderer.Stop();

            //Cleanup buffers
            Decoder.ClearBuffer();
            Renderer.ClearBuffer();
        }

        public void SeekTo(double percentLocation)
        {
            Decoder.SeekTo((float)(Decoder.VideoInfo.Duration.TotalSeconds * percentLocation));
        }

        public void Stop()
        {
            //Stop is pause with reset to begining
            Pause();

            //Go back to beggining
            Reset();
        }

        private void StopComparing()
        {
            try { _bufferMonitor.Abort(); }
            catch { }
        }

        private void Reset()
        {
            MostRecentFrameIndex = -1;

            if (_previousFrame != null)
            {
                _previousFrame.Dispose();
                _previousFrame = null;
            }
        }

        /// <summary>
        /// This should be called once per play start
        /// </summary>
        private void CompareFramesInBuffer()
        {
            while (true)
            {
                if (Decoder.IsBufferReady && Decoder.FramesInBuffer)
                {
                    var currentFrame = Decoder.FrameBuffer.First.Value;

                    if (_previousFrame != null)
                    {
                        var compareResult = Compare(currentFrame, _previousFrame);

                        //Notify of comparison results
                        if(FrameCompared != null)
                            FrameCompared(this, new FrameComparisonArgs() { Results = compareResult } );

                        //Set a limit on how many frames behind to render
                        if (Renderer.Queue.Count < 10)
                        {
                            //Add a cloned frame to rendering queue
                            Renderer.Queue.AddLast(new ComparedFrame()
                            {
                                Frame = currentFrame.Clone(),
                                ComparerResults = compareResult,
                            });
                        }

                        _previousFrame.Dispose();
                    }

                    //Retain location
                    MostRecentFrameIndex = currentFrame.FrameIndex;

                    _previousFrame = currentFrame;
                    Decoder.FrameBuffer.Remove(currentFrame);
                }
                else if (Decoder.AtEndOfVideo)
                {
                    Pause(false);

                    if (Stopped != null)
                        Stopped(this, null);

                    Reset();

                    //If no more frames and at the end, stop processing
                    return;
                }
                else
                {
                    Thread.Sleep(5);
                }
            }
        }

        public unsafe FrameComparerResults Compare(Frame bitmapA, Frame bitmapB)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var result = new FrameComparerResults()
            {
                Threshold = Threshold
            };

            //Performance optimizations
            var changedPixels = new List<Point>(bitmapA.Height * bitmapA.Width); //Pre-alloc all possible changed pix 
            var efficientTreshold = Threshold * 3;
            var aFirstPx = bitmapA.FirstPixelPointer;
            var bFirstPx = bitmapB.FirstPixelPointer;
            var height = bitmapA.Height;
            var width = bitmapA.Width;
            var stride = bitmapA.Stride;
            var changedPixelsCount = 0;

            //Do each row in parallel
            Parallel.For(0, height, new ParallelOptions() { /*MaxDegreeOfParallelism = 1*/ }, (int y) =>
            {
                var rowStart = stride * y; //Stride is width*3 bytes

                for(var x = 0; x < width; x++)
                {
                    var offset = x + x + x + rowStart;

                    var colorDifference =
                        Math.Abs(aFirstPx[offset + 0] - bFirstPx[offset + 0]) +
                        Math.Abs(aFirstPx[offset + 1] - bFirstPx[offset + 1]) +
                        Math.Abs(aFirstPx[offset + 2] - bFirstPx[offset + 2]);

                    if (colorDifference > efficientTreshold)
                    {
                        changedPixelsCount++;
                        changedPixels.Add(new Point(x, y));
                    }
                }
            });

            result.ChangedPixelsCount = changedPixelsCount;
            result.ChangedPixels = changedPixels;
            result.FrameIndex = bitmapB.FrameIndex;
            result.FrameTime = bitmapB.FrameTime;

            return result;
        }

    }
}
