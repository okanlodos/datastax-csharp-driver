﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Threading;
using System.Diagnostics;

namespace Cassandra
{
    public enum CompressionType { NoCompression, Snappy }
}

namespace Cassandra.Native
{
    internal enum BufferingMode { NoBuffering, FrameBuffering }

    internal partial class CassandraConnection
    {
        //static class Debug
        //{
        //    public static void WriteLine(string str)
        //    {
        //        Console.WriteLine(str);
        //    }
        //    public static void WriteLine(string str,string str2)
        //    {
        //        Console.WriteLine(str,":",str2);
        //    }
        //}
#if ERRORINJECTION
        public void KillSocket()
        {
            socket.Value.Shutdown(SocketShutdown.Both);
        }
#endif
        IPAddress serverAddress;
        int port;
        Guarded<Socket> socket = new Guarded<Socket>(null);
        Guarded<Stack<int>> freeStreamIDs = new Guarded<Stack<int>>(new Stack<int>());
        bool[] freeStreamIDtaken = new bool[byte.MaxValue + 1];
        AtomicValue<bool> isStreamOpened = new AtomicValue<bool>(false);

        object frameGuardier = new object();

        AtomicValue<Action<ResponseFrame>> frameEventCallback = new AtomicValue<Action<ResponseFrame>>(null);
        AtomicArray<Action<ResponseFrame>> frameReadCallback = new AtomicArray<Action<ResponseFrame>>(sbyte.MaxValue + 1);
        AtomicArray<AsyncResult<IOutput>> frameReadAsyncResult = new AtomicArray<AsyncResult<IOutput>>(sbyte.MaxValue + 1);

        Action<ResponseFrame> defaultFatalErrorAction;

        struct ErrorActionParam
        {
            public IResponse Response;
            public int streamId;
        }

        Action<ErrorActionParam> ProtocolErrorHandlerAction;

        AuthInfoProvider authInfoProvider;

        Session owner;

        void hostIsDown()
        {
            owner.hostIsDown(serverAddress);
        }

        internal CassandraConnection(Session owner, IPAddress serverAddress, int port, AuthInfoProvider authInfoProvider = null, CompressionType compression = CompressionType.NoCompression, int abortTimeout = Timeout.Infinite, bool noBufferingIfPossible = false)
        {
            this.owner = owner;
            bufferingMode = null;
            switch (compression)
            {
                case CompressionType.Snappy:
                    bufferingMode = new FrameBuffering();
                    break;
                case CompressionType.NoCompression:
                    bufferingMode = noBufferingIfPossible ? new NoBuffering() : new FrameBuffering();
                    break;
                default:
                    throw new ArgumentException();
            }

            this.authInfoProvider = authInfoProvider;
            if (compression == CompressionType.Snappy)
            {
                startupOptions.Add("COMPRESSION", "snappy");
                compressor = new SnappyProtoBufCompressor();
            }
            this.serverAddress = serverAddress;
            this.port = port;
            this.abortTimeout = abortTimeout;
            abortTimer = new Timer(abortTimerProc, null, Timeout.Infinite, Timeout.Infinite);
            createConnection();
            again();
        }

        private void createConnection()
        {
            lock (freeStreamIDs)
            {
                for (int i = 0; i <= sbyte.MaxValue; i++)
                {
                    freeStreamIDs.Value.Push(i);
                    freeStreamIDtaken[i] = false;
                }
            }

            ProtocolErrorHandlerAction = new Action<ErrorActionParam>((param) =>
               {
                   if (param.Response is ErrorResponse)
                       JobFinished(
                           param.streamId,
                           (param.Response as ErrorResponse).Output);
               });

            lock (frameGuardier)
                frameEventCallback.Value = new Action<ResponseFrame>(EventOccured);

            buffer = new byte[][] { 
                    new byte[bufferingMode.PreferedBufferSize()], 
                    new byte[bufferingMode.PreferedBufferSize()] };

            var newSock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            newSock.SendTimeout = this.abortTimeout;

            newSock.Connect(new IPEndPoint(serverAddress, port));
            socket.Value = newSock;
            bufferingMode.Reset();
            readerSocketStream = new NetworkStream(socket.Value);
            writerSocketStream = new NetworkStream(socket.Value);
        }

        byte[][] buffer = null;
        int bufNo = 0;

        private NetworkStream readerSocketStream;
        private NetworkStream writerSocketStream;

        IBuffering bufferingMode;

        private int allocateStreamId()
        {
            lock (freeStreamIDs)
            {
                if (freeStreamIDs.Value.Count > 0)
                {
                    int i = freeStreamIDs.Value.Pop();
                    freeStreamIDtaken[i] = true;
                    return i;
                }
                else
                    return -1;
            }
        }

        private void freeStreamId(int streamId)
        {
            lock (freeStreamIDs)
            {
                if (!freeStreamIDtaken[streamId])
                    return;
                freeStreamIDtaken[streamId] = false;
                freeStreamIDs.Value.Push(streamId);
                if (freeStreamIDs.Value.Count == sbyte.MaxValue + 1)
                    Debug.WriteLine("All streams are free");
            }
        }

        public bool isBusy(int max)
        {
            lock (freeStreamIDs)
                return sbyte.MaxValue + 1 - freeStreamIDs.Value.Count >= max;
        }

        public bool isFree(int min)
        {
            lock (freeStreamIDs)
                return sbyte.MaxValue + 1 - freeStreamIDs.Value.Count <= min;
        }

        public bool isEmpty()
        {
            lock (freeStreamIDs)
                return freeStreamIDs.Value.Count == sbyte.MaxValue + 1;
        }

        private void JobFinished(int streamId, IOutput outp)
        {
            lock (frameGuardier)
            {
                var ar = frameReadAsyncResult[streamId];
                frameReadAsyncResult[streamId] = null;
                ar.SetResult(outp);
                ar.Complete();
                freeStreamId(streamId);
                (outp as IWaitableForDispose).WaitForDispose();
            }
        }

        Dictionary<string, string> startupOptions = new Dictionary<string, string>()
        {
            {"CQL_VERSION","3.0.0"}
        };

        static FrameParser FrameParser = new FrameParser();

        internal class StreamAllocationException : Exception
        {
        }

        private AsyncResult<IOutput> BeginJob(AsyncCallback callback, object state, object owner, string propId, Action<int> job, bool startup = true)
        {
            var streamId = allocateStreamId();
            if (streamId == -1)
                throw new StreamAllocationException();

            defaultFatalErrorAction = new Action<ResponseFrame>((frame2) =>
            {
                var response2 = FrameParser.Parse(frame2);
                ProtocolErrorHandlerAction(new ErrorActionParam() { Response = response2, streamId = streamId });
            });

            var ar = new AsyncResult<IOutput>(callback, state, owner, propId);

            lock (frameGuardier)
                frameReadAsyncResult[streamId] = ar;

            try
            {
                if (startup && !isStreamOpened.Value)
                {
                    Evaluate(new StartupRequest(streamId, startupOptions), streamId, (frame) =>
                    {
                        var response = FrameParser.Parse(frame);
                        if (response is ReadyResponse)
                        {
                            isStreamOpened.Value = true;
                            job(streamId);
                        }
                        else if (response is AuthenticateResponse)
                        {
                            if (authInfoProvider == null)
                                throw new AuthenticationException("Credentials are required.", serverAddress);

                            var credentials = authInfoProvider.GetAuthInfos(serverAddress);

                            Evaluate(new CredentialsRequest(streamId, credentials), streamId, new Action<ResponseFrame>((frame2) =>
                            {
                                var response2 = FrameParser.Parse(frame2);
                                if (response2 is ReadyResponse)
                                {
                                    isStreamOpened.Value = true;
                                    job(streamId);
                                }
                                else
                                    ProtocolErrorHandlerAction(new ErrorActionParam() { Response = response2, streamId = streamId });
                            }));
                        }
                        else
                            ProtocolErrorHandlerAction(new ErrorActionParam() { Response = response, streamId = streamId });
                    });
                }
                else
                    job(streamId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message, "CassandraConnection.BeginJob");
                if (!setupWriterException(ex, streamId))
                    throw;
            }

            return ar;
        }

        IProtoBufComporessor compressor = null;

        private bool readerSocketExceptionOccured = false;
        private bool writerSocketExceptionOccured = false;

        public bool IsHealthy
        {
            get
            {
                lock (statusGuardier)
                {
                    return !alreadyDisposed && !readerSocketExceptionOccured && !writerSocketExceptionOccured;
                }
            }
        }

        Timer abortTimer = null;

        int abortTimeout = Timeout.Infinite;

        Guarded<bool> abortNotNeeded = new Guarded<bool>(false);

        private void abortTimerProc(object _)
        {
            lock (abortNotNeeded)
            {
                if (abortNotNeeded.Value)
                    return;
                abortNotNeeded.Value = true;
            }

            hostIsDown();

            lock (socket)
                if (socket.Value != null)
                {
                    try
                    {
                        readerSocketStream.Close();
                        writerSocketStream.Close();
                        socket.Value.Shutdown(SocketShutdown.Both);
                        socket.Value.Disconnect(false);
                    }
                    catch (ObjectDisposedException ex)
                    {
                        Debug.WriteLine(ex.Message, "CassandraConnection.abortTimer1");
                    }
                    catch (SocketException ex)
                    {
                        Debug.WriteLine(ex.Message, "CassandraConnection.abortTimer2");
                    }
                }
            if (setupReaderException(new CassandraConnectionTimeoutException()))
            {
                try
                {
                    bufferingMode.Close();
                }
                catch (CassandraConncectionIOException)
                {
                }
                lock (statusGuardier)
                    readerSocketExceptionOccured = true;
                again();
            }
        }

        Guarded<bool> readerSocketStreamBusy = new Guarded<bool>(false);

        internal void BeginReading()
        {
            lock (statusGuardier)
                if (alreadyDisposed)
                    return;
            try
            {
                if (!(bufferingMode is FrameBuffering))
                    lock (readerSocketStreamBusy)
                    {
                        while (readerSocketStreamBusy.Value)
                            Monitor.Wait(readerSocketStreamBusy);
                        readerSocketStreamBusy.Value = true;
                    }

                lock (readerSocketStream)
                {
                    var rh = readerSocketStream.BeginRead(buffer[bufNo], 0, buffer[bufNo].Length, new AsyncCallback((ar) =>
                    {
                        if (abortTimeout != Timeout.Infinite)
                        {
                            lock (abortNotNeeded)
                            {
                                if (abortNotNeeded.Value)
                                    return;
                                abortNotNeeded.Value = true;
                            }
                            abortTimer.Change(Timeout.Infinite, Timeout.Infinite);
                        }

                        try
                        {
                            int bytesReadCount;
                            lock (readerSocketStream)
                                bytesReadCount = readerSocketStream.EndRead(ar);

                            if (bytesReadCount == 0)
                            {
                                lock (statusGuardier)
                                    if (alreadyDisposed)
                                        return;
                                hostIsDown();
                                throw new CassandraConncectionIOException();
                            }
                            else
                            {
                                foreach (var frame in bufferingMode.Process(buffer[bufNo], bytesReadCount, readerSocketStream, compressor))
                                {
                                    Action<ResponseFrame> act = null;
                                    lock (frameGuardier)
                                    {
                                        if (frame.FrameHeader.streamId == 0xFF)
                                            act = frameEventCallback.Value;
                                        else if (frame.FrameHeader.streamId >= 0)
                                        {
                                            act = frameReadCallback[frame.FrameHeader.streamId];
                                            frameReadCallback[frame.FrameHeader.streamId] = null;
                                        }
                                    }

                                    if (act == null)
                                        throw new InvalidOperationException();
                                    //                                        act = defaultFatalErrorAction; //TODO: what to do here? this is a protocol valiation

                                    act.BeginInvoke(frame, (tar) =>
                                    {
                                        try
                                        {
                                            (tar.AsyncState as Action<ResponseFrame>).EndInvoke(tar);
                                        }
                                        catch (Exception ex)
                                        {
                                            Debug.WriteLine("BeginReading1");
                                            if (setupReaderException(ex))
                                            {
                                                hostIsDown();
                                                bufferingMode.Close();
                                                lock (statusGuardier)
                                                    readerSocketExceptionOccured = true;
                                            }
                                        }
                                        finally
                                        {
                                            if (!(bufferingMode is FrameBuffering))
                                                again();
                                        }
                                    }, act);
                                }
                                bufNo = 1 - bufNo;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("BeginReading2");
                            if (setupReaderException(ex))
                            {
                                hostIsDown();
                                bufferingMode.Close();
                                lock (statusGuardier)
                                    readerSocketExceptionOccured = true;
                            }
                        }
                        finally
                        {
                            if (bufferingMode is FrameBuffering)
                                again();
                            else
                            {
                                lock (readerSocketStreamBusy)
                                {
                                    readerSocketStreamBusy.Value = false;
                                    Monitor.PulseAll(readerSocketStreamBusy);
                                }
                                lock (statusGuardier)
                                    if (readerSocketExceptionOccured)
                                        again();
                            }
                        }
                    }), null);
                }
            }
            catch (IOException e)
            {
                Debug.WriteLine(e.Message, "CassandraConnection.BeginReading3");
                if (setupReaderException(e))
                {
                    hostIsDown();
                    bufferingMode.Close();
                    lock (statusGuardier)
                        readerSocketExceptionOccured = true;
                }
                else
                    throw;
            }
        }

        internal static bool IsStreamRelatedException(Exception ex)
        {
            return ex is SocketException
            || ex is CassandraConncectionIOException
            || ex is IOException
            || ex is ObjectDisposedException;
        }

        private bool setupReaderException(Exception ex)
        {
            isStreamOpened.Value = false;
            lock (frameGuardier)
            {
                for (int streamId = 0; streamId < sbyte.MaxValue + 1; streamId++)
                    if (frameReadAsyncResult[streamId] != null)
                    {
                        frameReadAsyncResult[streamId].Complete(ex);
                        freeStreamId(streamId);
                    }
            }
            return (ex.InnerException != null && IsStreamRelatedException(ex.InnerException)) || IsStreamRelatedException(ex);
        }

        object statusGuardier = new object();
        bool alreadyDisposed = false;

        private bool setupWriterException(Exception ex, int streamId)
        {
            lock (frameGuardier)
            {
                var ar = frameReadAsyncResult[streamId];
                freeStreamId(streamId);
                hostIsDown();
                lock (statusGuardier)
                    writerSocketExceptionOccured = true;
                ar.Complete(ex);
                return (ex.InnerException != null && IsStreamRelatedException(ex.InnerException)) || IsStreamRelatedException(ex);
            }
        }

        void checkDisposed()
        {
            lock (statusGuardier)
                if (alreadyDisposed)
                    throw new ObjectDisposedException("CassandraConnection");
        }

        public void Dispose()
        {
            lock (statusGuardier)
            {
                if (alreadyDisposed)
                    return;

                lock (socket)
                    if (socket.Value != null)
                    {
                        try
                        {
                            socket.Value.Shutdown(SocketShutdown.Both);
                            socket.Value.Disconnect(false);
                        }
                        catch (ObjectDisposedException ex)
                        {
                            Debug.WriteLine(ex.Message, "CassandraConnection.Dispose1");
                        }
                        catch (SocketException ex)
                        {
                            Debug.WriteLine(ex.Message, "CassandraConnection.Dispose2");
                        }
                    }

                alreadyDisposed = true;
            }
        }

        private void again()
        {
            if (IsHealthy)
            {
                BeginReading();
            }
            else
                Debug.WriteLine("!!!!");
        }


        ~CassandraConnection()
        {
            Dispose();
        }

        private void Evaluate(IRequest req, int streamId, Action<ResponseFrame> nextAction)
        {
            try
            {
                var frame = req.GetFrame();
                lock (writerSocketStream)
                {
                    lock (frameGuardier)
                        frameReadCallback[streamId] = nextAction;
                    writerSocketStream.Write(frame.buffer, 0, frame.buffer.Length);
                    writerSocketStream.Flush();
                    if (abortTimeout != Timeout.Infinite)
                    {
                        lock (abortNotNeeded)
                            abortNotNeeded.Value = false;
                        abortTimer.Change(abortTimeout, Timeout.Infinite);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message, "CassandraConnection.Evaluate");
                if (!setupWriterException(ex, streamId))
                    throw;
            }
        }

        internal IPAddress getAdress()
        {
            return serverAddress;
        }
    }
}
