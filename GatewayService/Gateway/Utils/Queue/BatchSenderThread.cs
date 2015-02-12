﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Gateway.Utils.MessageSender;
using Gateway.Utils.OperationStatus;
using System.Diagnostics;

namespace Gateway.Utils.Queue
{
    public class BatchSenderThread<T> : EventProcessor
    {
        private readonly IAsyncQueue<T> _DataSource;
        private readonly IMessageSender<T> _DataTarget;
        private int _outstandingTasks;

        private Thread _WorkerThread;
        private AutoResetEvent _operational;
        private AutoResetEvent _doWork;
        private bool _running; 


        private object _SyncRoot = new object();

        private static readonly string _LogMessagePrefix = "BatchSenderThread error. ";

        public BatchSenderThread(IAsyncQueue<T> dataSource, IMessageSender<T> dataTarget)
        {
            if (dataSource == null || dataTarget == null)
            {
                throw new ArgumentException("data source and data target cannot be null");
            }

            _operational = new AutoResetEvent(false);
            _doWork = new AutoResetEvent(false);
            _running = false;
            _DataSource = dataSource;
            _DataTarget = dataTarget;
            _outstandingTasks = 0;
        }

        public override bool Start()
        {
            bool start = false;

            lock (_SyncRoot)
            {
                if (_running == false)
                {
                    start = true;
                }
            }

            if (start)
            {
                _WorkerThread = new Thread(ThreadJob);
                _running = true;
                _WorkerThread.Start();
                return _operational.WaitOne();
            }

            return false;
        }

        public override bool Stop(int timeout)
        {
            bool stop = false;

            lock(_SyncRoot)
            {
                if (_running == true)
                {
                    // There must exist a worker thread
                    System.Diagnostics.Debug.Assert(_WorkerThread != null);

                    // signal the worker thread that exit is impending
                    _running = false;
                    _doWork.Set();

                    stop = true;
                }
            }

            if (stop) 
            {
                if (_operational.WaitOne(timeout) == false)
                {
                    // no other choice than forcing a stop
                    _WorkerThread.Abort();
                }

                _WorkerThread.Join();

                return true;
            }

            return false;
        }

        public override void Process()
        {
            _doWork.Set();
        }

        public event EventBatchProcessedEventHandler OnEventsBatchProcessed;

        private void ThreadJob()
        {
            // signal that the worker thread has actually started processing the events
            _operational.Set();

            try
            {
                // run until Stop() is called
                while (_running == true)
                {
                    try
                    {
                        // wait for some events to process
                        _doWork.WaitOne();

                        // check if we have been woken up to actually stop processing 
                        EventBatchProcessedEventHandler eventBatchProcessed = null;

                        lock (_SyncRoot)
                        {
                            if (_running == false)
                            {
                                return;
                            }

                            // take a snapshot of event handlers to invoke
                            eventBatchProcessed = OnEventsBatchProcessed;
                        }

                        // allocate enough space for the events currently in the queue
                        var tasks = new List<Task>();

                        // fish from the queue and accumulate, keep track of outstanding tasks to 
                        // avoid accumulating too many competing tasks
                        int count = _DataSource.Count - _outstandingTasks;
                        while (--count >= 0)
                        {
                            var t = _DataSource.TryPop();

                            Debug.Assert(_outstandingTasks >= 0);

                            Interlocked.Increment( ref _outstandingTasks);

                            tasks.Add( 
                                t.ContinueWith<Task>(popped =>
                                    {
                                        Interlocked.Decrement(ref _outstandingTasks);

                                        Debug.Assert(_outstandingTasks >= 0);

                                        if (popped.Result.IsSuccess)
                                        {
                                            return _DataTarget.SendMessage(popped.Result.Result);
                                        }
                                        return popped;
                                    })
                            );
                        }

                        // alert any client about outstanding message tasks
                        if (eventBatchProcessed != null)
                        {
                            Task.Run(() =>
                                {
                                    eventBatchProcessed(tasks);
                                });
                        }
                    }
                    catch (StackOverflowException ex)
                    {
                        if (Logger != null)
                        {
                            Logger.LogError(_LogMessagePrefix + ex.Message);
                        }

                        // do not hide stack overflow exceptions
                        throw;
                    }
                    catch (OutOfMemoryException ex)
                    {
                        if (Logger != null)
                        {
                            Logger.LogError(_LogMessagePrefix + ex.Message);
                        }

                        // do not hide memory exceptions
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (Logger != null)
                        {
                            Logger.LogError(_LogMessagePrefix + ex.Message);
                        }

                        // catch all other exceptions
                    }

                    // go and check for more events
                }
            }
            finally
            {
                // signal stop
                _operational.Set();
            }
        }
    }
}
