// Copyright 2020 Microsoft Corporation
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SPDY
{
	/// <summary>Implements a synchronization event that, when signaled, resets automatically after releasing a single waiting thread
	/// or task.
	/// </summary>
	sealed class AsyncAutoResetEvent : IDisposable
	{
		public AsyncAutoResetEvent() { }
		public AsyncAutoResetEvent(bool initialState) => set = initialState;

		public void Dispose() => e.Dispose(); // NOTE: waiters are not triggered when it's disposed, like the normal AutoResetEvent

		/// <summary>Sets the event, releasing a waiting thread or task.</summary>
		public void Set()
		{
			lock(e)
			{
				set = true;
				e.Set();
			}
		}

		/// <summary>Waits for the event to be set.</summary>
		public void Wait()
		{
			while(true)
			{
				lock(e)
				{
					if(set)
					{
						set = false;
						e.Reset();
						break;
					}
				}

				e.WaitOne();
			}
		}

		/// <summary>Returns a task that waits for the event to be set.</summary>
		/// <param name="cancelToken">A <see cref="CancellationToken"/> that can be used to cancel the wait.</param>
		public Task WaitAsync(CancellationToken cancelToken)
		{
			lock(e)
			{
				if(set)
				{
					set = false;
					e.Reset();
#if !NET45
					return Task.CompletedTask;
#else
					return Task.FromResult(false);
#endif
				}
			}

#if !NET45
			if(cancelToken.IsCancellationRequested) return Task.FromCanceled(cancelToken);
#else
			if(cancelToken.IsCancellationRequested) return CanceledTask;
#endif

			// if we need to wait, we'll register a callback on the thread pool that will attempt to complete the task
#if !NET45
			var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
#else
			var tcs = new TaskCompletionSource<object>();
#endif
			RegisteredWaitHandle tpreg;
			void callback(object ctx, bool timedOut)
			{
				var task = (TaskCompletionSource<object>)ctx;
				lock(e)
				{
					if(set && TrySetResult(task, null)) // if we consumed the event...
					{
						set = false; // reset it
					}
					else if(!task.Task.IsCanceled) // otherwise, if we haven't already been canceled, reregister for the event
					{
						tpreg = ThreadPool.UnsafeRegisterWaitForSingleObject(e, callback, ctx, Timeout.Infinite, true);
					}
				}
			}
			tpreg = ThreadPool.UnsafeRegisterWaitForSingleObject(e, callback, tcs, Timeout.Infinite, true);
			if(cancelToken.CanBeCanceled) // if the token can be canceled...
			{
#if NETCOREAPP3_0
				var ctreg = cancelToken.UnsafeRegister(ctx => // register a callback that unregisters our thread pool callback
#else
				var ctreg = cancelToken.Register(ctx => // register a callback that unregisters our thread pool callback
#endif
				{
					lock(e)
					{
						if(TrySetCanceled((TaskCompletionSource<object>)ctx)) tpreg.Unregister(null);
					}
				}, tcs);
				tcs.Task.ContinueWith((_, r) => ((CancellationTokenRegistration)r).Dispose(), ctreg);
			}
			return tcs.Task;
		}

		readonly AutoResetEvent e = new AutoResetEvent(false);
		bool set;

#if !NET45
		static bool TrySetCanceled(TaskCompletionSource<object> tcs) => tcs.TrySetCanceled();
		static bool TrySetResult(TaskCompletionSource<object> tcs, object result) => tcs.TrySetResult(result);
#else
		static bool TrySetCanceled(TaskCompletionSource<object> tcs)
		{
			Task.Run(() => tcs.TrySetCanceled()); // ensure continuations run asynchronously
			try { tcs.Task.Wait(); } // wait for the task (e.g. TrySetCanceled), not for continuations
			catch (AggregateException) { }
			return tcs.Task.IsCanceled;
		}

		static bool TrySetResult(TaskCompletionSource<object> tcs, object result)
		{
			Task.Run(() => tcs.TrySetResult(result)); // ensure continuations run asynchronously
			try { tcs.Task.Wait(); } // wait for the task (e.g. TrySetResult), not for continuations
			catch (AggregateException) { }
			return tcs.Task.Status == TaskStatus.RanToCompletion;
		}

		static Task CreateCanceledTask()
		{
			var tcs = new TaskCompletionSource<object>();
			tcs.SetCanceled();
			return tcs.Task;
		}

		static readonly Task CanceledTask = CreateCanceledTask();
#endif
	}
}
