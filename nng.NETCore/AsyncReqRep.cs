using nng.Native;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace nng
{
    using static nng.Native.Aio.UnsafeNativeMethods;
    using static nng.Native.Basic.UnsafeNativeMethods;
    using static nng.Native.Ctx.UnsafeNativeMethods;
    using static nng.Native.Msg.UnsafeNativeMethods;

    struct AsyncReqRespMsg<T>
    {
        public AsyncReqRespMsg(T message)
        {
            this.message = message;
            tcs = new TaskCompletionSource<T>();
        }
        internal T message;
        internal TaskCompletionSource<T> tcs;
    }

    public class ReqAsyncCtx<T> : AsyncCtx<T>, IReqRepAsyncContext<T>
    {
        public async Task<T> Send(T message)
        {
            System.Diagnostics.Debug.Assert(State == AsyncState.Init);
            if (State != AsyncState.Init)
            {
                await asyncMessage.tcs.Task;
            }
            asyncMessage = new AsyncReqRespMsg<T>(message);
            // Trigger the async send
            callback(IntPtr.Zero);
            return await asyncMessage.tcs.Task;
        }

        internal void callback(IntPtr arg)
        {
            var res = 0;
            switch (State)
            {
                case AsyncState.Init:
                    State = AsyncState.Send;
                    nng_aio_set_msg(aioHandle, Factory.Borrow(asyncMessage.message));
                    nng_ctx_send(ctxHandle, aioHandle);
                    break;
                
                case AsyncState.Send:
                    res = nng_aio_result(aioHandle);
                    if (res != 0)
                    {
                        Factory.Destroy(ref asyncMessage.message);
                        asyncMessage.tcs.TrySetNngError(res);
                        State = AsyncState.Init;
                        return;
                    }
                    State = AsyncState.Recv;
                    nng_ctx_recv(ctxHandle, aioHandle);
                    break;
                case AsyncState.Recv:
                    res = nng_aio_result(aioHandle);
                    if (res != 0)
                    {
                        asyncMessage.tcs.TrySetNngError(res);
                        State = AsyncState.Init;
                        return;
                    }
                    nng_msg msg = nng_aio_get_msg(aioHandle);
                    var message = Factory.CreateMessage(msg);
                    asyncMessage.tcs.SetResult(message);
                    State = AsyncState.Init;
                    break;
            }
        }

        AsyncReqRespMsg<T> asyncMessage;
    }

    class Request<T>
    {
        public T response;
        public TaskCompletionSource<T> requestTcs = new TaskCompletionSource<T>();
        public TaskCompletionSource<bool> replyTcs = new TaskCompletionSource<bool>();
    }

    public class RepAsyncCtx<T> : AsyncCtx<T>, IRepReqAsyncContext<T>
    {
        public static INngResult<IRepReqAsyncContext<T>> Create(IMessageFactory<T> factory, ISocket socket)
        {
            var ctx = new RepAsyncCtx<T>();
            var res = ctx.Init(factory, socket, ctx.callback);
            if (res == 0)
            {
                // Start receive loop
                ctx.callback(IntPtr.Zero);
                return NngResult.Ok<IRepReqAsyncContext<T>>(ctx);
            }
            else
            {
                return NngResult.Fail<IRepReqAsyncContext<T>>(res);
            }
        }

        public Task<T> Receive()
        {
            return asyncMessage.requestTcs.Task;
        }

        public Task<bool> Reply(T message)
        {
            System.Diagnostics.Debug.Assert(State == AsyncState.Wait);
            asyncMessage.response = message;
            // Move from wait to send state
            callback(IntPtr.Zero);
            return asyncMessage.replyTcs.Task;
        }

        internal void callback(IntPtr arg)
        {
            lock (sync)
            {
                var res = 0;
                switch (State)
                {
                    case AsyncState.Init:
                        init();
                        break;
                    case AsyncState.Recv:
                        res = nng_aio_result(aioHandle);
                        if (res != 0)
                        {
                            asyncMessage.requestTcs.TrySetNngError(res);
                            State = AsyncState.Recv;
                            return;
                        }
                        State = AsyncState.Wait;
                        nng_msg msg = nng_aio_get_msg(aioHandle);
                        var message = Factory.CreateMessage(msg);
                        asyncMessage.requestTcs.SetResult(message);
                        break;
                    case AsyncState.Wait:
                        nng_aio_set_msg(aioHandle, Factory.Borrow(asyncMessage.response));
                        State = AsyncState.Send;
                        nng_ctx_send(ctxHandle, aioHandle);
                        break;
                    case AsyncState.Send:
                        res = nng_aio_result(aioHandle);
                        if (res != 0)
                        {
                            Factory.Destroy(ref asyncMessage.response);
                            asyncMessage.replyTcs.TrySetNngError(res);
                        }
                        var currentReq = asyncMessage;
                        init();
                        currentReq.replyTcs.SetResult(true);
                        break;
                }
            }
        }

        void init()
        {
            asyncMessage = new Request<T>();
            State = AsyncState.Recv;
            nng_ctx_recv(ctxHandle, aioHandle);
        }

        private RepAsyncCtx(){}

        Request<T> asyncMessage;
        object sync = new object();
    }

}