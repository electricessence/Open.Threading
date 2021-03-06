﻿using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Open.Threading.Tasks
{
	public class AsyncQuery<TResult> : AsyncProcess
	{
#if NETSTANDARD2_1
		[AllowNull]
#endif
		TResult _latest = default!;

		protected new Func<Progress, TResult>? Closure
		{
			get;
			private set;
		}

		protected Task<TResult>? InternalTaskValued
		{
			get;
			private set;
		}

		public AsyncQuery(Func<Progress, TResult> query, TaskScheduler? scheduler = null)
			: base(scheduler)
		{
			Closure = query ?? throw new ArgumentNullException(nameof(query));
		}

		protected Task<TResult> EnsureProcessValued(bool once, TimeSpan? timeAllowedBeforeRefresh = null)
		{

			Task<TResult>? task = null;

			SyncLock!.ReadWriteConditionalOptimized(
				write =>
				{
					task = InternalTaskValued;
					return (task is null || !once && !task.IsActive()) // No action, or completed?
						&& (!timeAllowedBeforeRefresh.HasValue // Now?
							|| timeAllowedBeforeRefresh.Value < DateTime.Now - LatestCompleted); // Or later?
				}, () =>
				{

					task = new Task<TResult>(Process!, new Progress());
					task.Start(Scheduler);
					InternalTask = InternalTaskValued = task;
					Count++;

				}
			);

			// action could be null in some cases where timeAllowedBeforeRefresh condition is still met.
			return task!;
		}

		protected override Task EnsureProcess(bool once, TimeSpan? timeAllowedBeforeRefresh = null)
			=> EnsureProcessValued(once, timeAllowedBeforeRefresh);

		//long _processCount = 0;
#if NETSTANDARD2_1
		[return: MaybeNull]
#endif
		protected new TResult Process(object progress)
		{

			var p = (Progress)progress;
			try
			{
				//Contract.Assert(Interlocked.Increment(ref _processCount) == 1);
				var result = Closure!(p);
				Latest = result;
				return result;
			}
			catch (Exception ex)
			{
				SyncLock!.Write(() => LatestCompleted = DateTime.Now);
				p.Failed(ex.ToString());
			}
			//finally
			//{
			//	//Interlocked.Decrement(ref _processCount);
			//}
			return default!;
		}

		public bool IsCurrentDataReady
		{
			get
			{
				var t = InternalTask;
				if (t is null)
					return false;
				return !t.IsActive();
			}
		}

		public bool IsCurrentDataStale(TimeSpan timeAllowedBeforeStale)
		{
			return LatestCompleted.Add(timeAllowedBeforeStale) < DateTime.Now;
		}

		public override Progress Progress
		{
			get
			{
				var t = InternalTask;
				if (t != null) return (Progress)(t.AsyncState);
				var result = new Progress();
				if (IsLatestAvailable)
					result.Finish();
				return result;

			}
		}

		public virtual bool IsLatestAvailable
		{
			get;
			protected set;
		}

		protected virtual TResult GetLatest()
		{
			return _latest;
		}

		public virtual void OverrideLatest(TResult value, DateTime? completed = null)
		{
			SyncLock!.Write(() =>
			{
				_latest = value;
				LatestCompleted = completed ?? DateTime.Now;
				IsLatestAvailable = true;
			});
		}

		public virtual void OverrideLatest(TResult value, Func<TResult, TResult, bool> useNewValueEvaluator, DateTime? completed = null)
		{
			SyncLock!.ReadWriteConditionalOptimized(
				(write) => useNewValueEvaluator(_latest, value),
				() =>
				{
					_latest = value;
					LatestCompleted = completed ?? DateTime.Now;
					IsLatestAvailable = true;
				});
		}

		public TResult Latest
		{
			get => GetLatest();
			protected set => OverrideLatest(value);
		}


		public TResult LatestEnsured => GetLatestOrRunning(out _);

		public bool WaitForRunningToComplete(TimeSpan? waitForCurrentTimeout = null)
		{
			var task = SyncLock!.ReadValue(() => InternalTaskValued);
			if (task is null) return false;
			if (waitForCurrentTimeout.HasValue)
				task.Wait(waitForCurrentTimeout.Value);
			else
				task.Wait();
			return true;
		}


		public TResult RunningValue
		{
			get
			{
				var task = SyncLock!.ReadValue(() => InternalTaskValued);
				return task is null ? GetRunningValue() : task.Result;
			}
		}

		public TResult ActiveRunningValueOrLatestPossible
		{
			get
			{
				WaitForRunningToComplete();

				return HasBeenRun // This is in the case where possibly the app-pool has been reset.
					? LatestEnsured
					: GetRunningValue();
			}
		}

		public virtual bool TryGetLatest(
#if NETSTANDARD2_1
			[NotNullWhen(true)]
#endif
			out TResult latest,
			out DateTime completed)
		{
			var result = default(TResult);
			var resultComplete = DateTime.MinValue;
			var isReady = SyncLock!.ReadValue(() =>
			{
				result = _latest;
				resultComplete = LatestCompleted;
				return IsLatestAvailable;
			});
			latest = result!;
			completed = resultComplete;
			return isReady;
		}

		public virtual bool TryGetLatest(out TResult latest)
		{
			return TryGetLatest(out latest, out _);
		}

		public virtual bool TryGetLatestOrStart(out TResult latest, out DateTime completed)
		{
			var result = TryGetLatest(out latest, out completed);
			if (!result)
				EnsureProcessValued(true);
			return result;
		}

		public virtual bool TryGetLatestOrStart(out TResult latest)
		{
			return TryGetLatestOrStart(out latest, out _);
		}

		public virtual bool TryGetLatestOrStart()
			=> TryGetLatestOrStart(out _, out _);


		public TResult Refresh(TimeSpan? timeAllowedBeforeRefresh = null, TimeSpan? waitForCurrentTimeout = null)
		{
			EnsureProcessValued(false, timeAllowedBeforeRefresh);
			if (waitForCurrentTimeout.HasValue)
				WaitForRunningToComplete(waitForCurrentTimeout);
			return Latest;
		}

		public TResult RefreshNow(TimeSpan? waitForCurrentTimeout = null)
		{
			return Refresh(null, waitForCurrentTimeout);
		}

		// Will hold the requesting thread until the action is available.
		public TResult GetRunningValue()
		{
			return EnsureProcessValued(false).Result;
		}

		public TResult GetLatestOrRunning(out DateTime completed)
		{
			if (TryGetLatest(out var result, out completed)) return result;
			result = RunningValue;
			completed = DateTime.Now;
			return result;
		}



		protected override void OnDispose()
		{
			base.OnDispose();
			_latest = default!;
			Closure = null;
		}
	}
}
