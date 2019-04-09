using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Akka.Persistence.Oracle.Pattern
{
    /// <summary>
    /// Provides circuit breaker functionality to provide stability when working with
    /// "dangerous" operations, e.g. calls to remote systems
    ///
    ///<list type="bullet">
    ///<listheader>
    ///    <description>Transitions through three states:</description>
    ///</listheader>
    ///<item>
    ///    <term>In *Closed* state, </term>
    ///    <description>calls pass through until the maxFailures count is reached.
    ///         This causes the circuit breaker to open. Both exceptions and calls exceeding
    ///         callTimeout are considered failures.</description>
    ///</item>
    ///<item>
    ///    <term>In *Open* state, </term>
    ///    <description>calls fail-fast with an exception. After resetTimeout,
    ///         circuit breaker transitions to half-open state.</description>
    ///</item>
    ///<item>
    ///    <term>In *Half-Open* state, </term>
    ///    <description>the first call will be allowed through, if it succeeds
    ///         the circuit breaker will reset to closed state. If it fails, the circuit
    ///         breaker will re-open to open state. All calls beyond the first that execute
    ///         while the first is running will fail-fast with an exception.</description>
    ///</item>
    ///</list>
    /// </summary>
    internal class CircuitBreakerFixed
    {
        /// <summary>
        /// The current state of the breaker -- Closed, Half-Open, or Closed -- *access only via helper methods*
        /// </summary>
        private AtomicState _currentState;

        /// <summary>
        /// Helper method for access to the underlying state via Interlocked
        /// </summary>
        /// <param name="oldState">Previous state on transition</param>
        /// <param name="newState">Next state on transition</param>
        /// <returns>Whether the previous state matched correctly</returns>
        private bool SwapState(AtomicState oldState, AtomicState newState)
        {
            return Interlocked.CompareExchange(ref _currentState, newState, oldState) == oldState;
        }

        /// <summary>
        /// Helper method for access to the underlying state via Interlocked
        /// </summary>
        private AtomicState CurrentState
        {
            get
            {
                Interlocked.MemoryBarrier();
                return _currentState;
            }
        }

        /// <summary>
        /// TBD
        /// </summary>
        public int MaxFailures { get; private set; }

        /// <summary>
        /// TBD
        /// </summary>
        public TimeSpan CallTimeout { get; private set; }
        /// <summary>
        /// TBD
        /// </summary>
        public TimeSpan ResetTimeout { get; private set; }

        //akka.io implementation is to use nested static classes and access parent member variables
        //.Net static nested classes do not have access to parent member variables -- so we configure the states here and
        //swap them above
        private AtomicState Closed { get; set; }
        private AtomicState Open { get; set; }
        private AtomicState HalfOpen { get; set; }

        /// <summary>
        /// Create a new CircuitBreaker
        /// </summary>
        /// <param name="maxFailures">Maximum number of failures before opening the circuit</param>
        /// <param name="callTimeout"><see cref="TimeSpan"/> of time after which to consider a call a failure</param>
        /// <param name="resetTimeout"><see cref="TimeSpan"/> of time after which to attempt to close the circuit</param>
        /// <returns>TBD</returns>
        public static CircuitBreakerFixed Create(int maxFailures, TimeSpan callTimeout, TimeSpan resetTimeout)
        {
            return new CircuitBreakerFixed(maxFailures, callTimeout, resetTimeout);
        }

        /// <summary>
        /// Create a new CircuitBreaker
        /// </summary>
        /// <param name="maxFailures">Maximum number of failures before opening the circuit</param>
        /// <param name="callTimeout"><see cref="TimeSpan"/> of time after which to consider a call a failure</param>
        /// <param name="resetTimeout"><see cref="TimeSpan"/> of time after which to attempt to close the circuit</param>
        /// <returns>TBD</returns>
        public CircuitBreakerFixed(int maxFailures, TimeSpan callTimeout, TimeSpan resetTimeout)
        {
            MaxFailures = maxFailures;
            CallTimeout = callTimeout;
            ResetTimeout = resetTimeout;
            Closed = new Closed(this);
            Open = new Open(this);
            HalfOpen = new HalfOpen(this);
            _currentState = Closed;
            //_failures = new AtomicInteger();
        }

        /// <summary>
        /// Retrieves current failure count.
        /// </summary>
        public long CurrentFailureCount
        {
            get { return Closed.Current; }
        }

        /// <summary>
        /// Wraps invocation of asynchronous calls that need to be protected
        /// </summary>
        /// <typeparam name="T">TBD</typeparam>
        /// <param name="body">Call needing protected</param>
        /// <returns><see cref="Task"/> containing the call result</returns>
        public Task<T> WithCircuitBreaker<T>(Func<Task<T>> body)
        {
            return CurrentState.Invoke<T>(body);
        }

        /// <summary>
        /// Wraps invocation of asynchronous calls that need to be protected
        /// </summary>
        /// <param name="body">Call needing protected</param>
        /// <returns><see cref="Task"/></returns>
        public Task WithCircuitBreaker(Func<Task> body)
        {
            return CurrentState.Invoke(body);
        }

        /// <summary>
        /// The failure will be recorded farther down.
        /// </summary>
        /// <param name="body">TBD</param>
        public void WithSyncCircuitBreaker(Action body)
        {
            var cbTask = WithCircuitBreaker(() => Task.Factory.StartNew(body));
            if (!cbTask.Wait(CallTimeout))
            {
                //throw new TimeoutException( string.Format( "Execution did not complete within the time allotted {0} ms", CallTimeout.TotalMilliseconds ) );
            }
            if (cbTask.Exception != null)
            {
                ExceptionDispatchInfo.Capture(cbTask.Exception).Throw();
            }
        }

        /// <summary>
        /// Wraps invocations of asynchronous calls that need to be protected
        /// If this does not complete within the time allotted, it should return default(<typeparamref name="T"/>)
        ///
        /// <code>
        ///  Await.result(
        ///      withCircuitBreaker(try Future.successful(body) catch { case NonFatal(t) ⇒ Future.failed(t) }),
        ///      callTimeout)
        /// </code>
        ///
        /// </summary>
        /// <typeparam name="T">TBD</typeparam>
        /// <param name="body">TBD</param>
        /// <returns><typeparamref name="T"/> or default(<typeparamref name="T"/>)</returns>
        public T WithSyncCircuitBreaker<T>(Func<T> body)
        {
            var cbTask = WithCircuitBreaker(() => Task.Factory.StartNew(body));
            return cbTask.Wait(CallTimeout) ? cbTask.Result : default(T);
        }

        /// <summary>
        /// Adds a callback to execute when circuit breaker opens
        /// </summary>
        /// <param name="callback"><see cref="Action"/> Handler to be invoked on state change</param>
        /// <returns>CircuitBreaker for fluent usage</returns>
        public CircuitBreakerFixed OnOpen(Action callback)
        {
            Open.AddListener(callback);
            return this;
        }

        /// <summary>
        /// Adds a callback to execute when circuit breaker transitions to half-open
        /// </summary>
        /// <param name="callback"><see cref="Action"/> Handler to be invoked on state change</param>
        /// <returns>CircuitBreaker for fluent usage</returns>
        public CircuitBreakerFixed OnHalfOpen(Action callback)
        {
            HalfOpen.AddListener(callback);
            return this;
        }

        /// <summary>
        /// Adds a callback to execute when circuit breaker state closes
        /// </summary>
        /// <param name="callback"><see cref="Action"/> Handler to be invoked on state change</param>
        /// <returns>CircuitBreaker for fluent usage</returns>
        public CircuitBreakerFixed OnClose(Action callback)
        {
            Closed.AddListener(callback);
            return this;
        }

        /// <summary>
        /// Implements consistent transition between states. Throws IllegalStateException if an invalid transition is attempted.
        /// </summary>
        /// <param name="fromState">State being transitioning from</param>
        /// <param name="toState">State being transitioned to</param>
        /// <exception cref="IllegalStateException">
        /// This exception is thrown if an invalid transition is attempted from <paramref name="fromState"/> to <paramref name="toState"/>.
        /// </exception>
        private void Transition(AtomicState fromState, AtomicState toState)
        {
            if (SwapState(fromState, toState))
            {
                Debug.WriteLine("Successful transition from {0} to {1}", fromState, toState);
                toState.Enter();
            }
            // else some other thread already swapped state
        }

        /// <summary>
        /// Trips breaker to an open state. This is valid from Closed or Half-Open states
        /// </summary>
        /// <param name="fromState">State we're coming from (Closed or Half-Open)</param>
        internal void TripBreaker(AtomicState fromState)
        {
            Transition(fromState, Open);
        }

        /// <summary>
        /// Resets breaker to a closed state.  This is valid from an Half-Open state only.
        /// </summary>
        internal void ResetBreaker()
        {
            Transition(HalfOpen, Closed);
        }

        /// <summary>
        /// Attempts to reset breaker by transitioning to a half-open state.  This is valid from an Open state only.
        /// </summary>
        internal void AttemptReset()
        {
            Transition(Open, HalfOpen);
        }

        //private readonly Task timeoutTask = Task.FromResult(new TimeoutException("Circuit Breaker Timed out."));
    }

    /// <summary>
    /// Concrete implementation of Open state
    /// </summary>
    internal class Open : AtomicState
    {
        private readonly CircuitBreakerFixed _breaker;

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="breaker">TBD</param>
        public Open(CircuitBreakerFixed breaker)
            : base(breaker.CallTimeout, 0)
        {
            _breaker = breaker;
        }

        /// <summary>
        /// N/A
        /// </summary>
        /// <typeparam name="T">N/A</typeparam>
        /// <param name="body">N/A</param>
        /// <exception cref="OpenCircuitException">This exception is thrown automatically since the circuit is open.</exception>
        /// <returns>N/A</returns>
        public override async Task<T> Invoke<T>(Func<Task<T>> body)
        {
            throw new OpenCircuitException();
        }

        /// <summary>
        /// N/A
        /// </summary>
        /// <param name="body">N/A</param>
        /// <exception cref="OpenCircuitException">This exception is thrown automatically since the circuit is open.</exception>
        /// <returns>N/A</returns>
        public override async Task Invoke(Func<Task> body)
        {
            throw new OpenCircuitException();
        }

        /// <summary>
        /// No-op for open, calls are never executed so cannot succeed or fail
        /// </summary>
        protected override void CallFails()
        {
            //throw new NotImplementedException();
        }

        /// <summary>
        /// No-op for open, calls are never executed so cannot succeed or fail
        /// </summary>
        protected override void CallSucceeds()
        {
            //throw new NotImplementedException();
        }

        /// <summary>
        /// On entering this state, schedule an attempted reset and store the entry time to
        /// calculate remaining time before attempted reset.
        /// </summary>
        protected override void EnterInternal()
        {
            Task.Delay(_breaker.ResetTimeout).ContinueWith(task => _breaker.AttemptReset());
        }
    }

    /// <summary>
    /// Concrete implementation of half-open state
    /// </summary>
    internal class HalfOpen : AtomicState
    {
        private readonly CircuitBreakerFixed _breaker;
        private readonly AtomicBoolean _lock;

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="breaker">TBD</param>
        public HalfOpen(CircuitBreakerFixed breaker)
            : base(breaker.CallTimeout, 0)
        {
            _breaker = breaker;
            _lock = new AtomicBoolean();
        }

        /// <summary>
        /// Allows a single call through, during which all other callers fail-fast. If the call fails, the breaker reopens.
        /// If the call succeeds, the breaker closes.
        /// </summary>
        /// <typeparam name="T">TBD</typeparam>
        /// <param name="body">Implementation of the call that needs protected</param>
        /// <exception cref="OpenCircuitException">TBD</exception>
        /// <returns><see cref="Task"/> containing result of protected call</returns>
        public override async Task<T> Invoke<T>(Func<Task<T>> body)
        {
            if (!_lock.CompareAndSet(true, false))
            {
                throw new OpenCircuitException();
            }
            return await CallThrough(body);
        }

        /// <summary>
        /// Allows a single call through, during which all other callers fail-fast. If the call fails, the breaker reopens.
        /// If the call succeeds, the breaker closes.
        /// </summary>
        /// <param name="body">Implementation of the call that needs protected</param>
        /// <exception cref="OpenCircuitException">TBD</exception>
        /// <returns><see cref="Task"/> containing result of protected call</returns>
        public override async Task Invoke(Func<Task> body)
        {
            if (!_lock.CompareAndSet(true, false))
            {
                throw new OpenCircuitException();
            }
            await CallThrough(body);
        }

        /// <summary>
        /// Reopen breaker on failed call.
        /// </summary>
        protected override void CallFails()
        {
            _breaker.TripBreaker(this);
        }

        /// <summary>
        /// Reset breaker on successful call.
        /// </summary>
        protected override void CallSucceeds()
        {
            _breaker.ResetBreaker();
        }

        /// <summary>
        /// On entry, guard should be reset for that first call to get in
        /// </summary>
        protected override void EnterInternal()
        {
            _lock.Value = true;
        }

        /// <summary>
        /// Override for more descriptive toString
        /// </summary>
        /// <returns>TBD</returns>
        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "Half-Open currently testing call for success = {0}", (_lock == true));
        }
    }

    /// <summary>
    /// Concrete implementation of Closed state
    /// </summary>
    internal class Closed : AtomicState
    {
        private readonly CircuitBreakerFixed _breaker;

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="breaker">TBD</param>
        public Closed(CircuitBreakerFixed breaker)
            : base(breaker.CallTimeout, 0)
        {
            _breaker = breaker;
        }

        /// <summary>
        /// Implementation of invoke, which simply attempts the call
        /// </summary>
        /// <typeparam name="T">TBD</typeparam>
        /// <param name="body">Implementation of the call that needs protected</param>
        /// <returns><see cref="Task"/> containing result of protected call</returns>
        public override Task<T> Invoke<T>(Func<Task<T>> body)
        {
            return CallThrough(body);
        }

        /// <summary>
        /// Implementation of invoke, which simply attempts the call
        /// </summary>
        /// <param name="body">Implementation of the call that needs protected</param>
        /// <returns><see cref="Task"/> containing result of protected call</returns>
        public override Task Invoke(Func<Task> body)
        {
            return CallThrough(body);
        }

        /// <summary>
        /// On failed call, the failure count is incremented.  The count is checked against the configured maxFailures, and
        /// the breaker is tripped if we have reached maxFailures.
        /// </summary>
        protected override void CallFails()
        {
            if (IncrementAndGet() == _breaker.MaxFailures)
            {
                _breaker.TripBreaker(this);
            }
        }

        /// <summary>
        /// On successful call, the failure count is reset to 0
        /// </summary>
        protected override void CallSucceeds()
        {
            Reset();
        }

        /// <summary>
        /// On entry of this state, failure count is reset.
        /// </summary>
        protected override void EnterInternal()
        {
            Reset();
        }

        /// <summary>
        /// Returns a <see cref="System.String" /> that represents this instance.
        /// </summary>
        /// <returns>
        /// A <see cref="System.String" /> that represents this instance.
        /// </returns>
        public override string ToString()
        {
            return $"Closed with failure count = {Current}";
        }
    }

    /// <summary>
    /// This exception is thrown when the CircuitBreaker is open.
    /// </summary>
    public class OpenCircuitException : AkkaException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OpenCircuitException"/> class.
        /// </summary>
        public OpenCircuitException() : base("Circuit Breaker is open; calls are failing fast") { }

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenCircuitException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        public OpenCircuitException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenCircuitException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="cause">The exception that is the cause of the current exception.</param>
        public OpenCircuitException(string message, Exception cause)
            : base(message, cause)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenCircuitException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="SerializationInfo" /> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="StreamingContext" /> that contains contextual information about the source or destination.</param>
        protected OpenCircuitException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    /// <summary>
    /// This exception provides the base for all Akka.NET specific exceptions within the system.
    /// </summary>
    public abstract class AkkaException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AkkaException"/> class.
        /// </summary>
        protected AkkaException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AkkaException"/> class.
        /// </summary>
        /// <param name="message">The message that describes the error.</param>
        /// <param name="cause">The exception that is the cause of the current exception.</param>
        protected AkkaException(string message, Exception cause = null)
            : base(message, cause)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AkkaException"/> class.
        /// </summary>
        /// <param name="info">The <see cref="SerializationInfo"/> that holds the serialized object data about the exception being thrown.</param>
        /// <param name="context">The <see cref="StreamingContext"/> that contains contextual information about the source or destination.</param>
        protected AkkaException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }

        /// <summary>
        /// The exception that is the cause of the current exception.
        /// </summary>
        protected Exception Cause { get { return InnerException; } }
    }

    /// <summary>
    /// Internal state abstraction
    /// </summary>
    internal abstract class AtomicState : AtomicCounterLong, IAtomicState
    {
        private readonly ConcurrentQueue<Action> _listeners;
        private readonly TimeSpan _callTimeout;

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="callTimeout">TBD</param>
        /// <param name="startingCount">TBD</param>
        protected AtomicState(TimeSpan callTimeout, long startingCount)
            : base(startingCount)
        {
            _listeners = new ConcurrentQueue<Action>();
            _callTimeout = callTimeout;
        }

        /// <summary>
        /// Add a listener function which is invoked on state entry
        /// </summary>
        /// <param name="listener">listener implementation</param>
        public void AddListener(Action listener)
        {
            _listeners.Enqueue(listener);
        }

        /// <summary>
        /// Test for whether listeners exist
        /// </summary>
        public bool HasListeners
        {
            get { return !_listeners.IsEmpty; }
        }

        /// <summary>
        /// Notifies the listeners of the transition event via a 
        /// </summary>
        /// <returns>TBD</returns>
        protected async Task NotifyTransitionListeners()
        {
            if (!HasListeners) return;
            await Task
                .Factory
                .StartNew
                (
                    () =>
                    {
                        foreach (var listener in _listeners)
                        {
                            listener.Invoke();
                        }
                    }
                ).ConfigureAwait(false);
        }

        /// <summary>
        /// Shared implementation of call across all states.  Thrown exception or execution of the call beyond the allowed
        /// call timeout is counted as a failed call, otherwise a successful call
        /// 
        /// NOTE: In .Net there is no way to cancel an uncancellable task. We are merely cancelling the wait and marking this
        /// as a failure.
        /// 
        /// see http://blogs.msdn.com/b/pfxteam/archive/2011/11/10/10235834.aspx 
        /// </summary>
        /// <typeparam name="T">TBD</typeparam>
        /// <param name="task">Implementation of the call</param>
        /// <returns>result of the call</returns>
        public async Task<T> CallThrough<T>(Func<Task<T>> task)
        {
            var deadline = DateTime.UtcNow.Add(_callTimeout);
            ExceptionDispatchInfo capturedException = null;
            T result = default(T);
            try
            {
                result = await task().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                capturedException = ExceptionDispatchInfo.Capture(ex);
            }

            bool throwException = capturedException != null;
            if (throwException || DateTime.UtcNow.CompareTo(deadline) >= 0)
            {
                CallFails();
                if (throwException)
                    capturedException.Throw();
            }
            else
            {
                CallSucceeds();
            }
            return result;
        }

        /// <summary>
        /// Shared implementation of call across all states.  Thrown exception or execution of the call beyond the allowed
        /// call timeout is counted as a failed call, otherwise a successful call
        /// 
        /// NOTE: In .Net there is no way to cancel an uncancellable task. We are merely cancelling the wait and marking this
        /// as a failure.
        /// 
        /// see http://blogs.msdn.com/b/pfxteam/archive/2011/11/10/10235834.aspx 
        /// </summary>
        /// <param name="task"><see cref="Task"/> Implementation of the call</param>
        /// <returns><see cref="Task"/></returns>
        public async Task CallThrough(Func<Task> task)
        {
            var deadline = DateTime.UtcNow.Add(_callTimeout);
            ExceptionDispatchInfo capturedException = null;

            try
            {
                await task().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                capturedException = ExceptionDispatchInfo.Capture(ex);
            }

            bool throwException = capturedException != null;
            if (throwException || DateTime.UtcNow.CompareTo(deadline) >= 0)
            {
                CallFails();
                if (throwException) capturedException.Throw();
            }
            else
            {
                CallSucceeds();
            }


        }

        /// <summary>
        /// Abstract entry point for all states
        /// </summary>
        /// <typeparam name="T">TBD</typeparam>
        /// <param name="body">Implementation of the call that needs protected</param>
        /// <returns><see cref="Task"/> containing result of protected call</returns>
        public abstract Task<T> Invoke<T>(Func<Task<T>> body);

        /// <summary>
        /// Abstract entry point for all states
        /// </summary>
        /// <param name="body">Implementation of the call that needs protected</param>
        /// <returns><see cref="Task"/> containing result of protected call</returns>
        public abstract Task Invoke(Func<Task> body);

        /// <summary>
        /// Invoked when call fails
        /// </summary>
        protected abstract void CallFails();

        /// <summary>
        /// Invoked when call succeeds
        /// </summary>
        protected abstract void CallSucceeds();

        /// <summary>
        /// Invoked on the transitioned-to state during transition. Notifies listeners after invoking subclass template method _enter
        /// </summary>
        protected abstract void EnterInternal();

        /// <summary>
        /// Enter the state. NotifyTransitionListeners is not awaited -- its "fire and forget". 
        /// It is up to the user to handle any errors that occur in this state.
        /// </summary>
        public void Enter()
        {
            EnterInternal();
            NotifyTransitionListeners();
        }

    }

    /// <summary>
    /// This interface represents the parts of the internal circuit breaker state; the behavior stack, watched by, watching and termination queue
    /// </summary>
    public interface IAtomicState
    {
        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="listener">TBD</param>
        void AddListener(Action listener);
        /// <summary>
        /// TBD
        /// </summary>
        bool HasListeners { get; }
        /// <summary>
        /// TBD
        /// </summary>
        /// <typeparam name="T">TBD</typeparam>
        /// <param name="body">TBD</param>
        /// <returns>TBD</returns>
        Task<T> Invoke<T>(Func<Task<T>> body);
        /// <summary>
        /// TBD
        /// </summary>
        void Enter();
    }

    /// <summary>
    /// An atomic 64 bit integer counter.
    /// </summary>
    public class AtomicCounterLong : IAtomicCounter<long>
    {
        /// <summary>
        /// Creates an instance of an AtomicCounterLong.
        /// </summary>
        /// <param name="value">The initial value of this counter.</param>
        public AtomicCounterLong(long value)
        {
            _value = value;
        }

        /// <summary>
        /// Creates an instance of an AtomicCounterLong with a starting value of -1.
        /// </summary>
        public AtomicCounterLong()
        {
            _value = -1;
        }

        /// <summary>
        /// The current value for this counter.
        /// </summary>
        private long _value;

        /// <summary>
        /// Retrieves the current value of the counter
        /// </summary>
        public long Current { get { return Interlocked.Read(ref _value); } }

        /// <summary>
        /// Increments the counter and returns the next value.
        /// </summary>
        /// <returns>TBD</returns>
        public long Next()
        {
            return Interlocked.Increment(ref _value);
        }

        /// <summary>
        /// Atomically increments the counter by one.
        /// </summary>
        /// <returns>The original value.</returns>
        public long GetAndIncrement()
        {
            var nextValue = Next();
            return nextValue - 1;
        }

        /// <summary>
        /// Atomically increments the counter by one.
        /// </summary>
        /// <returns>The new value.</returns>
        public long IncrementAndGet()
        {
            var nextValue = Next();
            return nextValue;
        }

        /// <summary>
        /// Atomically decrements the counter by one
        /// </summary>
        /// <returns>The new value</returns>
        public long DecrementAndGet()
        {
            return Interlocked.Decrement(ref _value);
        }

        /// <summary>
        /// Gets the current value of the counter and adds an amount to it.
        /// </summary>
        /// <remarks>This uses a CAS loop as Interlocked.Increment is not atomic for longs on 32bit systems.</remarks>
        /// <param name="amount">The amount to add to the counter.</param>
        /// <returns>The original value.</returns>
        public long GetAndAdd(long amount)
        {
            var newValue = Interlocked.Add(ref _value, amount);
            return newValue - amount;
        }

        /// <summary>
        /// Adds an amount to the counter and returns the new value.
        /// </summary>
        /// <remarks>This uses a CAS loop as Interlocked.Increment is not atomic for longs on 32bit systems.</remarks>
        /// <param name="amount">The amount to add to the counter.</param>
        /// <returns>The new counter value.</returns>
        public long AddAndGet(long amount)
        {
            var newValue = Interlocked.Add(ref _value, amount);
            return newValue;
        }

        /// <summary>
        /// Resets the counter to zero.
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _value, 0);
        }

        /// <summary>
        /// Returns current counter value and sets a new value on it's place in one operation.
        /// </summary>
        /// <param name="value">TBD</param>
        /// <returns>TBD</returns>
        public long GetAndSet(long value)
        {
            return Interlocked.Exchange(ref _value, value);
        }

        /// <summary>
        /// Compares current counter value with provided <paramref name="expected"/> value,
        /// and sets it to <paramref name="newValue"/> if compared values where equal.
        /// Returns true if replacement has succeed.
        /// </summary>
        /// <param name="expected">TBD</param>
        /// <param name="newValue">TBD</param>
        /// <returns>TBD</returns>
        public bool CompareAndSet(long expected, long newValue)
        {
            return Interlocked.CompareExchange(ref _value, newValue, expected) != _value;
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <returns>TBD</returns>
        public override string ToString()
        {
            return Current.ToString();
        }
    }

        /// <summary>
    /// An interface that describes a numeric counter.
    /// </summary>
    /// <typeparam name="T">The type of the numeric.</typeparam>
    public interface IAtomicCounter<T>
    {
        /// <summary>
        /// The current value of this counter.
        /// </summary>
        T Current { get; }

        /// <summary>
        /// Increments the counter and gets the next value. This is exactly the same as calling <see cref="IncrementAndGet"/>.
        /// </summary>
        T Next();

        /// <summary>
        /// Atomically increments the counter by one.
        /// </summary>
        /// <returns>The original value.</returns>
        T GetAndIncrement();

        /// <summary>
        /// Atomically increments the counter by one.
        /// </summary>
        /// <returns>The new value.</returns>
        T IncrementAndGet();

        /// <summary>
        /// Returns the current value and adds the specified value to the counter.
        /// </summary>
        /// <param name="amount">The amount to add to the counter.</param>
        /// <returns>The original value before additions.</returns>
        T GetAndAdd(T amount);

        /// <summary>
        /// Adds the specified value to the counter and returns the new value.
        /// </summary>
        /// <param name="amount">The amount to add to the counter.</param>
        /// <returns>The new value after additions.</returns>
        T AddAndGet(T amount);

        /// <summary>
        /// Resets the counter to zero.
        /// </summary>
        void Reset();
    }

        /// <summary>
    /// Implementation of the java.concurrent.util.AtomicBoolean type.
    /// 
    /// Uses <see cref="Interlocked.MemoryBarrier"/> internally to enforce ordering of writes
    /// without any explicit locking. .NET's strong memory on write guarantees might already enforce
    /// this ordering, but the addition of the MemoryBarrier guarantees it.
    /// </summary>
    public class AtomicBoolean
    {
        private const int _falseValue = 0;
        private const int _trueValue = 1;

        private int _value;
        /// <summary>
        /// Sets the initial value of this <see cref="AtomicBoolean"/> to <paramref name="initialValue"/>.
        /// </summary>
        /// <param name="initialValue">TBD</param>
        public AtomicBoolean(bool initialValue = false)
        {
            _value = initialValue ? _trueValue : _falseValue;
        }

        /// <summary>
        /// The current value of this <see cref="AtomicReference{T}"/>
        /// </summary>
        public bool Value
        {
            get
            {
                Interlocked.MemoryBarrier();
                return _value==_trueValue;
            }
            set
            {
                Interlocked.Exchange(ref _value, value ? _trueValue : _falseValue);    
            }
        }

        /// <summary>
        /// If <see cref="Value"/> equals <paramref name="expected"/>, then set the Value to
        /// <paramref name="newValue"/>.
        /// </summary>
        /// <param name="expected">TBD</param>
        /// <param name="newValue">TBD</param>
        /// <returns><c>true</c> if <paramref name="newValue"/> was set</returns>
        public bool CompareAndSet(bool expected, bool newValue)
        {
            var expectedInt = expected ? _trueValue : _falseValue;
            var newInt = newValue ? _trueValue : _falseValue;
            return Interlocked.CompareExchange(ref _value, newInt, expectedInt) == expectedInt;
        }

        /// <summary>
        /// Atomically sets the <see cref="Value"/> to <paramref name="newValue"/> and returns the old <see cref="Value"/>.
        /// </summary>
        /// <param name="newValue">The new value</param>
        /// <returns>The old value</returns>
        public bool GetAndSet(bool newValue)
        {
            return Interlocked.Exchange(ref _value, newValue ? _trueValue : _falseValue) == _trueValue;
        }

        #region Conversion operators

        /// <summary>
        /// Performs an implicit conversion from <see cref="AtomicBoolean"/> to <see cref="System.Boolean"/>.
        /// </summary>
        /// <param name="atomicBoolean">The boolean to convert</param>
        /// <returns>The result of the conversion.</returns>
        public static implicit operator bool(AtomicBoolean atomicBoolean)
        {
            return atomicBoolean.Value;
        }

        /// <summary>
        /// Performs an implicit conversion from <see cref="System.Boolean"/> to <see cref="AtomicBoolean"/>.
        /// </summary>
        /// <param name="value">The boolean to convert</param>
        /// <returns>The result of the conversion.</returns>
        public static implicit operator AtomicBoolean(bool value)
        {
            return new AtomicBoolean(value);
        }

        #endregion
    }
}
