using System;
using System.Threading;
using System.Threading.Tasks;

namespace ThorsKist.Sync
{
    // Serializes candidate creation, durable commit, and installation. No network
    // or inventory callbacks run here. Awaiting callers may publish only returned
    // results. M4 supplies authorization, admission and safe checkpoint cutoffs.
    public sealed class JournalWriter : IDisposable
    {
        private readonly IJournalStorage storage;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private JournalMachineState state;
        private bool faulted, disposed;
        private JournalWriter(IJournalStorage storage, JournalMachineState state) { this.storage = storage; this.state = state; }
        public static Task<JournalWriter> OpenAsync(IJournalStorage storage, VaultState initial, CancellationToken token = default) => Task.Run(async () =>
        {
            var state = await storage.ReadStateAsync(token).ConfigureAwait(false);
            if (state == null)
            {
                state = JournalMachineState.Create(initial);
                await storage.CommitAsync(new StorageCommit(0, state), token).ConfigureAwait(false);
            }
            if (!state.Current.Identity.Matches(initial.Identity)) throw new InvalidOperationException("Writer vault mismatch");
            return new JournalWriter(storage, state);
        }, token);

        public Task<CommandTransition> ExecuteAsync(ItemCommand command, CancellationToken token = default) => Run(async () =>
        {
            var transition = JournalMachine.Execute(state, command);
            if (!ReferenceEquals(state, transition.State)) await Install(transition.State, token).ConfigureAwait(false);
            return transition;
        }, token);
        public Task<JournalMachineState> AppendNoOpAsync(CancellationToken token = default) => Run(async () =>
        { await Install(JournalMachine.AppendNoOp(state), token).ConfigureAwait(false); return state; }, token);
        public Task<JournalMachineState> CheckpointAsync(long safeCutoff, CancellationToken token = default) => Run(async () =>
        { await Install(JournalMachine.Checkpoint(state, safeCutoff), token).ConfigureAwait(false); return state; }, token);
        public Task<JournalMachineState> ReadAsync(CancellationToken token = default) => Run(() => Task.FromResult(state), token);
        internal Task<JournalMachineState> TransitionAsync(Func<JournalMachineState, JournalMachineState> transition) => Run(async () =>
        {
            JournalMachineState next;
            using (JournalDiagnostics.Measure("writer.transition.compute")) next = transition(state);
            if (!ReferenceEquals(next, state)) await Install(next, default).ConfigureAwait(false); return state;
        }, default);
        private async Task Install(JournalMachineState next, CancellationToken token)
        {
            using var elapsed = JournalDiagnostics.Measure("writer.commit.elapsed", JournalTimingKind.Elapsed, JournalTimingThread.Any, next.Current.Stacks.Count);
            try { await storage.CommitAsync(new StorageCommit(state, next), token).ConfigureAwait(false); state = next; }
            catch { faulted = true; throw; }
        }
        private Task<T> Run<T>(Func<Task<T>> action, CancellationToken token)
        {
            var scheduled = JournalDiagnostics.Measure("writer.schedule.wait", JournalTimingKind.Wait, JournalTimingThread.Any);
            return Task.Run(async () =>
        {
            scheduled.Dispose();
            using (JournalDiagnostics.Measure("writer.gate.wait", JournalTimingKind.Wait, JournalTimingThread.Any))
                await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (disposed) throw new ObjectDisposedException(nameof(JournalWriter));
                if (faulted) throw new InvalidOperationException("Storage outcome uncertain; reopen and recover writer");
                token.ThrowIfCancellationRequested(); return await action().ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }, token);
        }
        public void Dispose() { gate.Wait(); try { disposed = true; storage.Dispose(); } finally { gate.Release(); } }
    }
}
