using System.Collections.Concurrent;
using System.Net;
using B3.Trading.Application;
using B3.Trading.Domain;
using B3.Trading.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sdk = B3.EntryPoint.Client;
using Up = B3.EntryPoint.Client.Models;

namespace B3.Trading.Application.Tests;

public sealed class B3EntryPointClientGatewayReceiptTests
{
    private const string FirmId = "FIRM_A";
    private const string FrameHash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void PublicConstructor_PreservesLegacyBinarySignature()
    {
        var constructor = Assert.Single(typeof(B3EntryPointClientGateway).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.Equal(23, parameters.Length);
        Assert.Equal("eventStreamOverride", parameters[^1].Name);
        Assert.True(parameters[^1].HasDefaultValue);
    }

    [Fact]
    public void ReceiptMapping_PreservesExactFirmSessionSequenceAndFrameIdentity()
    {
        var sdkReceipt = new Up.OutboundAttemptReceipt(
            Frame(Up.OutboundOperationKind.NewOrder, clOrdId: 101, seq: 19),
            Up.OutboundAttemptStage.TransportWriteCompleted);

        var receipt = B3EntryPointClientGateway.MapReceipt(sdkReceipt, FirmId);

        Assert.Equal(ExchangeGatewayReceipt.CurrentVersion, receipt.Version);
        Assert.Equal(FirmId, receipt.Frame.FirmId);
        Assert.Equal(42UL, receipt.Frame.SessionId);
        Assert.Equal(7u, receipt.Frame.SessionVerId);
        Assert.Equal(19UL, receipt.Frame.OutboundSeqNum);
        Assert.Equal(101UL, receipt.Frame.ClOrdId);
        Assert.Equal(ExchangeGatewayOperation.NewOrder, receipt.Frame.Operation);
        Assert.Equal(128, receipt.Frame.EncodedFrameLength);
        Assert.Equal(FrameHash, receipt.Frame.EncodedFrameSha256);
        Assert.Equal(ExchangeGatewayAttemptStage.TransportWriteCompleted, receipt.LastStage);
        Assert.DoesNotContain(
            Enum.GetNames<ExchangeGatewayAttemptStage>(),
            name => name.Contains("Accepted", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Delivered", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Sent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CallbackFailure_IsTypedProvenUnsent_AndCannotWrite()
    {
        var writes = 0;
        await using var gateway = await BuildGatewayAsync(
            submit: async (_, callback, ct) =>
            {
                var frame = Frame(Up.OutboundOperationKind.NewOrder, 101, 1);
                try
                {
                    await callback(frame, ct);
                }
                catch (Exception ex)
                {
                    throw AttemptFailure(
                        Up.OutboundAttemptStage.SequenceReservedAndEncoded,
                        frame,
                        ex);
                }

                Interlocked.Increment(ref writes);
                return new Up.OutboundAttemptReceipt(
                    frame, Up.OutboundAttemptStage.TransportWriteCompleted);
            });

        var ex = await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() =>
            gateway.SubmitWithReceiptAsync(
                Order(101),
                (_, _) => ValueTask.FromException(new IOException("ledger unavailable")),
                CancellationToken.None));

        Assert.Equal(ExchangeGatewayFailureDisposition.OutboundProvenUnsent, ex.Disposition);
        Assert.Equal(ExchangeGatewayAttemptStage.SequenceReservedAndEncoded, ex.LastStage);
        Assert.NotNull(ex.Frame);
        Assert.Equal(0, Volatile.Read(ref writes));

        await gateway.SubmitWithReceiptAsync(
            Order(101),
            (_, _) => ValueTask.CompletedTask,
            CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref writes));
    }

    [Fact]
    public async Task CallbackCancellation_IsTypedProvenUnsent_AndCannotWrite()
    {
        var writes = 0;
        await using var gateway = await BuildGatewayAsync(
            cancel: async (_, callback, ct) =>
            {
                var frame = Frame(Up.OutboundOperationKind.Cancel, 102, 1);
                try
                {
                    await callback(frame, ct);
                }
                catch (Exception ex)
                {
                    throw AttemptFailure(
                        Up.OutboundAttemptStage.SequenceReservedAndEncoded,
                        frame,
                        ex);
                }

                Interlocked.Increment(ref writes);
                return new Up.OutboundAttemptReceipt(
                    frame, Up.OutboundAttemptStage.TransportWriteCompleted);
            });
        using var callbackCts = new CancellationTokenSource();
        callbackCts.Cancel();

        var ex = await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() =>
            gateway.CancelWithReceiptAsync(
                Order(100),
                102,
                (_, _) => ValueTask.FromCanceled(callbackCts.Token),
                CancellationToken.None));

        Assert.True(ex.NoTransportWritePossible);
        Assert.Equal(0, Volatile.Read(ref writes));
    }

    [Theory]
    [InlineData(Up.OutboundAttemptStage.NotStarted, true, ExchangeGatewayAttemptStage.NotStarted)]
    [InlineData(Up.OutboundAttemptStage.SequenceReserved, true, ExchangeGatewayAttemptStage.SequenceReserved)]
    [InlineData(Up.OutboundAttemptStage.SequenceReservedAndEncoded, true, ExchangeGatewayAttemptStage.SequenceReservedAndEncoded)]
    [InlineData(Up.OutboundAttemptStage.FramePrepared, true, ExchangeGatewayAttemptStage.FramePrepared)]
    [InlineData(Up.OutboundAttemptStage.TransportWriteStarted, false, ExchangeGatewayAttemptStage.TransportWriteStarted)]
    [InlineData(Up.OutboundAttemptStage.TransportWriteCompleted, false, ExchangeGatewayAttemptStage.TransportWriteCompleted)]
    public void TypedFailureMapping_UsesOnlySdkNoWriteProof(
        Up.OutboundAttemptStage sdkStage,
        bool provenUnsent,
        ExchangeGatewayAttemptStage expectedStage)
    {
        var frame = sdkStage < Up.OutboundAttemptStage.SequenceReservedAndEncoded
            ? null
            : Frame(Up.OutboundOperationKind.Replace, 103, 3);
        var sdkException = AttemptFailure(sdkStage, frame, new IOException("typed SDK failure"));

        var mapped = B3EntryPointClientGateway.MapAttemptException(sdkException, FirmId);

        Assert.Equal(expectedStage, mapped.LastStage);
        Assert.Equal(
            provenUnsent
                ? ExchangeGatewayFailureDisposition.OutboundProvenUnsent
                : ExchangeGatewayFailureDisposition.Ambiguous,
            mapped.Disposition);
        Assert.Equal(provenUnsent, mapped.NoTransportWritePossible);
    }

    [Fact]
    public async Task CancellationAfterFramePrepared_UsesTypedProof_ButSessionFailsClosedForReconciliation()
    {
        var attempts = 0;
        await using var gateway = await BuildGatewayAsync(
            replace: async (_, callback, ct) =>
            {
                Interlocked.Increment(ref attempts);
                var frame = Frame(Up.OutboundOperationKind.Replace, 103, 4);
                await callback(frame, ct);
                throw AttemptFailure(
                    Up.OutboundAttemptStage.FramePrepared,
                    frame,
                    new OperationCanceledException(ct));
            });

        var ex = await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() =>
            gateway.CancelReplaceWithReceiptAsync(
                Order(100),
                103,
                80,
                31m,
                null,
                null,
                null,
                (_, _) => ValueTask.CompletedTask,
                CancellationToken.None));

        Assert.Equal(ExchangeGatewayFailureDisposition.OutboundProvenUnsent, ex.Disposition);
        Assert.Equal(ExchangeGatewayAttemptStage.FramePrepared, ex.LastStage);
        var blocked = await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() =>
            gateway.CancelReplaceWithReceiptAsync(
                Order(100),
                104,
                70,
                31m,
                null,
                null,
                null,
                (_, _) => ValueTask.CompletedTask,
                CancellationToken.None));
        Assert.Equal(ExchangeGatewayAttemptStage.NotStarted, blocked.LastStage);
        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    [Theory]
    [InlineData(Up.OutboundAttemptStage.TransportWriteStarted)]
    [InlineData(Up.OutboundAttemptStage.TransportWriteCompleted)]
    public async Task WriteOrPersistenceFailure_IsAmbiguous_AndFailsClosed(
        Up.OutboundAttemptStage failureStage)
    {
        var attempts = 0;
        await using var gateway = await BuildGatewayAsync(
            submit: async (_, callback, ct) =>
            {
                Interlocked.Increment(ref attempts);
                var frame = Frame(Up.OutboundOperationKind.NewOrder, 101, 5);
                await callback(frame, ct);
                throw AttemptFailure(
                    failureStage,
                    frame,
                    new IOException(failureStage == Up.OutboundAttemptStage.TransportWriteCompleted
                        ? "session state persistence failed"
                        : "partial socket write"));
            });

        var ex = await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() =>
            gateway.SubmitWithReceiptAsync(
                Order(101),
                (_, _) => ValueTask.CompletedTask,
                CancellationToken.None));

        Assert.Equal(ExchangeGatewayFailureDisposition.Ambiguous, ex.Disposition);
        Assert.False(ex.NoTransportWritePossible);
        var blocked = await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() =>
            gateway.SubmitWithReceiptAsync(
                Order(102),
                (_, _) => ValueTask.CompletedTask,
                CancellationToken.None));
        Assert.Equal(ExchangeGatewayAttemptStage.NotStarted, blocked.LastStage);
        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task SuccessfulWrite_ReturnsTransportCompletion_NotVenueAcceptance()
    {
        var callbackCompleted = false;
        var writeObservedCallback = false;
        await using var gateway = await BuildGatewayAsync(
            submit: async (_, callback, ct) =>
            {
                var frame = Frame(Up.OutboundOperationKind.NewOrder, 101, 6);
                await callback(frame, ct);
                writeObservedCallback = callbackCompleted;
                return new Up.OutboundAttemptReceipt(
                    frame, Up.OutboundAttemptStage.SdkSessionStatePersisted);
            });

        var receipt = await gateway.SubmitWithReceiptAsync(
            Order(101),
            (_, _) =>
            {
                callbackCompleted = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(writeObservedCallback);
        Assert.Equal(ExchangeGatewayAttemptStage.SdkSessionStatePersisted, receipt.LastStage);
        Assert.True(receipt.LastStage >= ExchangeGatewayAttemptStage.TransportWriteCompleted);
    }

    [Fact]
    public async Task GenericFailure_IsNotGuessedIntoTypedNoWriteEvidence()
    {
        var failure = new IOException("untyped failure");
        await using var gateway = await BuildGatewayAsync(
            submit: (_, _, _) => Task.FromException<Up.OutboundAttemptReceipt>(failure));

        var actual = await Assert.ThrowsAsync<IOException>(() =>
            gateway.SubmitWithReceiptAsync(
                Order(101),
                (_, _) => ValueTask.CompletedTask,
                CancellationToken.None));

        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task ConcurrentMixedOperations_PreservePreparedAndWireSequenceOrder()
    {
        var nextSeq = 0L;
        var active = 0;
        var entered = new ConcurrentQueue<Up.OutboundOperationKind>();
        var prepared = new List<ulong>();
        var written = new ConcurrentQueue<ulong>();
        var firstPrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Up.OutboundAttemptReceipt> Send(
            Up.OutboundOperationKind operation,
            ulong clOrdId,
            Up.OutboundFramePreparedCallback callback,
            CancellationToken ct)
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            try
            {
                entered.Enqueue(operation);
                var seq = (ulong)Interlocked.Increment(ref nextSeq);
                var frame = Frame(operation, clOrdId, seq);
                await callback(frame, ct);
                written.Enqueue(seq);
                return new Up.OutboundAttemptReceipt(
                    frame, Up.OutboundAttemptStage.TransportWriteCompleted);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        await using var gateway = await BuildGatewayAsync(
            submit: (request, callback, ct) =>
                Send(Up.OutboundOperationKind.NewOrder, request.ClOrdID.Value, callback, ct),
            cancel: (request, callback, ct) =>
                Send(Up.OutboundOperationKind.Cancel, request.ClOrdID.Value, callback, ct),
            replace: (request, callback, ct) =>
                Send(Up.OutboundOperationKind.Replace, request.ClOrdID.Value, callback, ct));

        async ValueTask Prepared(ExchangeGatewayFrameIdentity frame, CancellationToken _)
        {
            prepared.Add(frame.OutboundSeqNum);
            if (frame.OutboundSeqNum == 1)
            {
                firstPrepared.SetResult();
                await releaseFirst.Task;
            }
        }

        var submit = gateway.SubmitWithReceiptAsync(Order(101), Prepared, CancellationToken.None);
        var cancel = gateway.CancelWithReceiptAsync(Order(100), 102, Prepared, CancellationToken.None);
        var replace = gateway.CancelReplaceWithReceiptAsync(
            Order(100), 103, 75, 31m, null, null, null, Prepared, CancellationToken.None);

        await firstPrepared.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(cancel.IsCompleted);
        Assert.False(replace.IsCompleted);
        releaseFirst.SetResult();

        var receipts = await Task.WhenAll(submit, cancel, replace);

        Assert.Equal(
            new[]
            {
                Up.OutboundOperationKind.NewOrder,
                Up.OutboundOperationKind.Cancel,
                Up.OutboundOperationKind.Replace,
            },
            entered);
        Assert.Equal(new ulong[] { 1, 2, 3 }, prepared);
        Assert.Equal(new ulong[] { 1, 2, 3 }, written);
        Assert.Equal(new ulong[] { 1, 2, 3 }, receipts.Select(r => r.Frame.OutboundSeqNum));
        Assert.Equal(
            new[]
            {
                ExchangeGatewayOperation.NewOrder,
                ExchangeGatewayOperation.Cancel,
                ExchangeGatewayOperation.Replace,
            },
            receipts.Select(r => r.Frame.Operation));
    }

    [Fact]
    public async Task LegacySend_HoldsAdapterGate_AgainstReceiptSend()
    {
        var legacyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLegacy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiptEntries = 0;
        await using var gateway = await BuildGatewayAsync(
            legacySubmit: async (_, ct) =>
            {
                legacyEntered.SetResult();
                await releaseLegacy.Task.WaitAsync(ct);
            },
            cancel: async (request, callback, ct) =>
            {
                Interlocked.Increment(ref receiptEntries);
                var frame = Frame(Up.OutboundOperationKind.Cancel, request.ClOrdID.Value, 2);
                await callback(frame, ct);
                return new Up.OutboundAttemptReceipt(
                    frame, Up.OutboundAttemptStage.TransportWriteCompleted);
            });

        var legacy = gateway.SubmitAsync(Order(101), CancellationToken.None);
        await legacyEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var receipt = gateway.CancelWithReceiptAsync(
            Order(100),
            102,
            (_, _) => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.False(receipt.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref receiptEntries));

        releaseLegacy.SetResult();
        await legacy;
        await receipt;
        Assert.Equal(1, Volatile.Read(ref receiptEntries));
    }

    [Fact]
    public async Task StatePersistenceFailure_DelayedFailClose_BlocksQueuedReceiptAndLegacySends()
    {
        var sdkFailureReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSdkFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failCloseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedReceiptEntries = 0;
        var queuedLegacyEntries = 0;
        await using var gateway = await BuildGatewayAsync(
            submit: async (request, callback, ct) =>
            {
                var frame = Frame(Up.OutboundOperationKind.NewOrder, request.ClOrdID.Value, 1);
                await callback(frame, ct);
                sdkFailureReady.SetResult();
                await releaseSdkFailure.Task.WaitAsync(ct);
                throw AttemptFailure(
                    Up.OutboundAttemptStage.TransportWriteCompleted,
                    frame,
                    new IOException("session state persistence failed"));
            },
            cancel: async (request, callback, ct) =>
            {
                Interlocked.Increment(ref queuedReceiptEntries);
                var frame = Frame(Up.OutboundOperationKind.Cancel, request.ClOrdID.Value, 2);
                await callback(frame, ct);
                return new Up.OutboundAttemptReceipt(
                    frame, Up.OutboundAttemptStage.TransportWriteCompleted);
            },
            legacyReplace: (_, _) =>
            {
                Interlocked.Increment(ref queuedLegacyEntries);
                return Task.CompletedTask;
            },
            beforeFailClose: async () =>
            {
                failCloseEntered.SetResult();
                await releaseFailClose.Task;
            });

        var failed = gateway.SubmitWithReceiptAsync(
            Order(101),
            (_, _) => ValueTask.CompletedTask,
            CancellationToken.None);
        await sdkFailureReady.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var queuedReceipt = gateway.CancelWithReceiptAsync(
            Order(100),
            102,
            (_, _) => ValueTask.CompletedTask,
            CancellationToken.None);
        var queuedLegacy = gateway.CancelReplaceAsync(
            Order(100), 103, 90, 31m, null, null, null, CancellationToken.None);

        Assert.False(queuedReceipt.IsCompleted);
        Assert.False(queuedLegacy.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref queuedReceiptEntries));
        Assert.Equal(0, Volatile.Read(ref queuedLegacyEntries));

        releaseSdkFailure.SetResult();
        await failCloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(gateway.OutboundReconciliationRequired);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.CompleteOutboundReconciliationAsync(CancellationToken.None));

        releaseFailClose.SetResult();

        var failure = await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() => failed);
        Assert.Equal(ExchangeGatewayFailureDisposition.Ambiguous, failure.Disposition);
        var receiptBlocked = await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(
            () => queuedReceipt);
        Assert.Equal(ExchangeGatewayAttemptStage.NotStarted, receiptBlocked.LastStage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queuedLegacy);
        Assert.Equal(0, Volatile.Read(ref queuedReceiptEntries));
        Assert.Equal(0, Volatile.Read(ref queuedLegacyEntries));
    }

    [Fact]
    public async Task FailClose_TerminateDrainCompletion_SuppressesEventLoopReconnect()
    {
        var eventLoopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEventLoop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failCloseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectCalls = 0;

        async IAsyncEnumerable<Up.EntryPointEvent> Events(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            eventLoopEntered.SetResult();
            await releaseEventLoop.Task.WaitAsync(ct);
            yield break;
        }

        await using var gateway = await BuildGatewayAsync(
            submit: async (request, callback, ct) =>
            {
                var frame = Frame(Up.OutboundOperationKind.NewOrder, request.ClOrdID.Value, 1);
                await callback(frame, ct);
                throw AttemptFailure(
                    Up.OutboundAttemptStage.TransportWriteCompleted,
                    frame,
                    new IOException("session state persistence failed"));
            },
            beforeFailClose: async () =>
            {
                failCloseEntered.SetResult();
                await releaseFailClose.Task;
            },
            reconnect: (_, _, _) =>
            {
                Interlocked.Increment(ref reconnectCalls);
                return Task.FromResult(new Sdk.ReconnectOutcome(
                    Sdk.ReconnectKind.Reattached, 7, 0, 0, true));
            },
            events: Events,
            startEventLoop: true);

        await eventLoopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var failed = gateway.SubmitWithReceiptAsync(
            Order(101),
            (_, _) => ValueTask.CompletedTask,
            CancellationToken.None);
        await failCloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(gateway.OutboundReconciliationRequired);
        releaseEventLoop.SetResult();
        await gateway.EventLoopTaskForTests!.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, Volatile.Read(ref reconnectCalls));
        Assert.False(gateway.IsReconnecting);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.ConnectAsync(CancellationToken.None));

        releaseFailClose.SetResult();
        await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() => failed);
    }

    [Fact]
    public async Task ScheduledReconnectRace_CannotReopenAfterOutboundFailClose()
    {
        var eventLoopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEventLoop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiptPrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReceiptFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failCloseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedLegacyEntries = 0;
        var queuedReceiptEntries = 0;

        async IAsyncEnumerable<Up.EntryPointEvent> Events(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            eventLoopEntered.SetResult();
            await releaseEventLoop.Task.WaitAsync(ct);
            yield break;
        }

        await using var gateway = await BuildGatewayAsync(
            submit: async (request, callback, ct) =>
            {
                var frame = Frame(Up.OutboundOperationKind.NewOrder, request.ClOrdID.Value, 1);
                await callback(frame, ct);
                receiptPrepared.SetResult();
                await releaseReceiptFailure.Task.WaitAsync(ct);
                throw AttemptFailure(
                    Up.OutboundAttemptStage.TransportWriteCompleted,
                    frame,
                    new IOException("session state persistence failed"));
            },
            cancel: async (request, callback, ct) =>
            {
                Interlocked.Increment(ref queuedReceiptEntries);
                var frame = Frame(Up.OutboundOperationKind.Cancel, request.ClOrdID.Value, 2);
                await callback(frame, ct);
                return new Up.OutboundAttemptReceipt(
                    frame, Up.OutboundAttemptStage.TransportWriteCompleted);
            },
            legacyReplace: (_, _) =>
            {
                Interlocked.Increment(ref queuedLegacyEntries);
                return Task.CompletedTask;
            },
            beforeFailClose: async () =>
            {
                failCloseEntered.SetResult();
                await releaseFailClose.Task;
            },
            reconnect: async (_, _, ct) =>
            {
                reconnectEntered.SetResult();
                await releaseReconnect.Task.WaitAsync(ct);
                return new Sdk.ReconnectOutcome(
                    Sdk.ReconnectKind.Reattached, 7, 0, 0, true);
            },
            events: Events,
            startEventLoop: true);

        await eventLoopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var failed = gateway.SubmitWithReceiptAsync(
            Order(101),
            (_, _) => ValueTask.CompletedTask,
            CancellationToken.None);
        await receiptPrepared.Task.WaitAsync(TimeSpan.FromSeconds(2));

        releaseEventLoop.SetResult();
        await reconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseReceiptFailure.SetResult();
        await failCloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var queuedReceipt = gateway.CancelWithReceiptAsync(
            Order(100), 102, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        var queuedLegacy = gateway.CancelReplaceAsync(
            Order(100), 103, 90, 31m, null, null, null, CancellationToken.None);
        Assert.False(queuedReceipt.IsCompleted);
        Assert.False(queuedLegacy.IsCompleted);

        releaseReconnect.SetResult();
        await gateway.ScheduledReconnectTaskForTests!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(gateway.OutboundReconciliationRequired);
        Assert.Equal(0, Volatile.Read(ref queuedReceiptEntries));
        Assert.Equal(0, Volatile.Read(ref queuedLegacyEntries));

        releaseFailClose.SetResult();
        await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() => failed);
        await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() => queuedReceipt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queuedLegacy);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.ConnectAsync(CancellationToken.None));
        Assert.Equal(0, Volatile.Read(ref queuedReceiptEntries));
        Assert.Equal(0, Volatile.Read(ref queuedLegacyEntries));
    }

    [Fact]
    public async Task ExplicitReconciliationReset_RequiresFreshSession_AndFailureStaysClosed()
    {
        var resetAttempts = 0;
        var legacyEntries = 0;
        var receiptEntries = 0;
        await using var gateway = await BuildGatewayAsync(
            submit: async (request, callback, ct) =>
            {
                var frame = Frame(Up.OutboundOperationKind.NewOrder, request.ClOrdID.Value, 1);
                await callback(frame, ct);
                throw AttemptFailure(
                    Up.OutboundAttemptStage.TransportWriteCompleted,
                    frame,
                    new IOException("session state persistence failed"));
            },
            cancel: async (request, callback, ct) =>
            {
                Interlocked.Increment(ref receiptEntries);
                var frame = Frame(Up.OutboundOperationKind.Cancel, request.ClOrdID.Value, 1);
                await callback(frame, ct);
                return new Up.OutboundAttemptReceipt(
                    frame, Up.OutboundAttemptStage.TransportWriteCompleted);
            },
            legacyCancel: (_, _) =>
            {
                Interlocked.Increment(ref legacyEntries);
                return Task.CompletedTask;
            },
            reconnect: (mode, _, _) =>
            {
                Assert.Equal(Sdk.ReconnectMode.AlwaysNegotiate, mode);
                if (Interlocked.Increment(ref resetAttempts) == 1)
                    throw new IOException("fresh-session reset failed");
                return Task.FromResult(new Sdk.ReconnectOutcome(
                    Sdk.ReconnectKind.Renegotiated, 8, 0, 0, false));
            });

        await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() =>
            gateway.SubmitWithReceiptAsync(
                Order(101), (_, _) => ValueTask.CompletedTask, CancellationToken.None));

        Assert.True(gateway.OutboundReconciliationRequired);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.ConnectAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.CancelAsync(Order(100), 102, CancellationToken.None));

        await Assert.ThrowsAsync<IOException>(
            () => gateway.CompleteOutboundReconciliationAsync(CancellationToken.None));
        Assert.True(gateway.OutboundReconciliationRequired);
        Assert.Equal(0, Volatile.Read(ref legacyEntries));

        await gateway.CompleteOutboundReconciliationAsync(CancellationToken.None);

        Assert.False(gateway.OutboundReconciliationRequired);
        Assert.Equal(8u, gateway.CurrentSessionVerId);
        await gateway.CancelAsync(Order(100), 102, CancellationToken.None);
        await gateway.CancelWithReceiptAsync(
            Order(100), 103, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref legacyEntries));
        Assert.Equal(1, Volatile.Read(ref receiptEntries));
    }

    [Fact]
    public async Task FramePreparedCallback_ReentrantLegacySend_FailsInsteadOfDeadlocking()
    {
        var legacyEntries = 0;
        B3EntryPointClientGateway? gateway = null;
        gateway = await BuildGatewayAsync(
            submit: async (request, callback, ct) =>
            {
                var frame = Frame(Up.OutboundOperationKind.NewOrder, request.ClOrdID.Value, 1);
                try
                {
                    await callback(frame, ct);
                }
                catch (Exception ex)
                {
                    throw AttemptFailure(
                        Up.OutboundAttemptStage.SequenceReservedAndEncoded,
                        frame,
                        ex);
                }

                return new Up.OutboundAttemptReceipt(
                    frame, Up.OutboundAttemptStage.TransportWriteCompleted);
            },
            legacyCancel: (_, _) =>
            {
                Interlocked.Increment(ref legacyEntries);
                return Task.CompletedTask;
            });
        await using var ownedGateway = gateway;

        var failure = await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(
            () => gateway.SubmitWithReceiptAsync(
                Order(101),
                async (_, ct) => await gateway.CancelAsync(Order(100), 102, ct),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(ExchangeGatewayAttemptStage.SequenceReservedAndEncoded, failure.LastStage);
        Assert.True(failure.NoTransportWritePossible);
        Assert.Equal(0, Volatile.Read(ref legacyEntries));
    }

    [Fact]
    public async Task FailureLogging_DoesNotIncludeFrameHashOrOrderIdentityFields()
    {
        var logger = new CapturingLogger();
        await using var gateway = await BuildGatewayAsync(
            logger,
            submit: async (_, callback, ct) =>
            {
                var frame = Frame(Up.OutboundOperationKind.NewOrder, 101, 7);
                await callback(frame, ct);
                throw AttemptFailure(
                    Up.OutboundAttemptStage.TransportWriteStarted,
                    frame,
                    new IOException("socket failure"));
            });
        var order = new Order(
            101,
            new EndClientId("secret-owner"),
            "SECRET-SYMBOL",
            12345,
            OrderSide.Buy,
            OrderType.Limit,
            100,
            30m,
            FirmId);

        await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() =>
            gateway.SubmitWithReceiptAsync(
                order,
                (_, _) => ValueTask.CompletedTask,
                CancellationToken.None));

        var logs = string.Join('\n', logger.Messages);
        Assert.DoesNotContain(FrameHash, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-owner", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET-SYMBOL", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnavailableAndCompatibilityGateways_DoNotFabricateFrameEvidence()
    {
        var unavailable = new UnavailableExchangeGateway();
        var callbackCalled = false;
        var unavailableFailure = await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() =>
            unavailable.SubmitWithReceiptAsync(
                Order(101),
                (_, _) =>
                {
                    callbackCalled = true;
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None));

        Assert.False(callbackCalled);
        Assert.True(unavailableFailure.NoTransportWritePossible);
        Assert.Null(unavailableFailure.Frame);

        var legacy = new LegacyOnlyGateway();
        IExchangeGateway compatibility = legacy;
        await compatibility.SubmitAsync(Order(101), CancellationToken.None);
        var compatibilityFailure = await Assert.ThrowsAsync<ExchangeGatewayAttemptException>(() =>
            compatibility.SubmitWithReceiptAsync(
                Order(102),
                (_, _) => ValueTask.CompletedTask,
                CancellationToken.None));

        Assert.Equal(1, legacy.SubmitCalls);
        Assert.True(compatibilityFailure.NoTransportWritePossible);
        Assert.Null(compatibilityFailure.Frame);
    }

    [Fact]
    public async Task MultiFirmGateway_ForwardsReceiptCallbackToTheOrderFirm()
    {
        var gateway = await BuildGatewayAsync(
            cancel: async (request, callback, ct) =>
            {
                var frame = Frame(
                    Up.OutboundOperationKind.Cancel,
                    request.ClOrdID.Value,
                    8);
                await callback(frame, ct);
                return new Up.OutboundAttemptReceipt(
                    frame, Up.OutboundAttemptStage.TransportWriteCompleted);
            });
        await using var registry = new FirmGatewayRegistry([gateway]);
        var multiFirm = new MultiFirmExchangeGateway(registry);
        ExchangeGatewayFrameIdentity? prepared = null;

        var receipt = await multiFirm.CancelWithReceiptAsync(
            Order(100),
            105,
            (frame, _) =>
            {
                prepared = frame;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(prepared, receipt.Frame);
        Assert.Equal(FirmId, receipt.Frame.FirmId);
        Assert.Equal(105UL, receipt.Frame.ClOrdId);
    }

    private static Up.OutboundFrameIdentity Frame(
        Up.OutboundOperationKind operation,
        ulong clOrdId,
        ulong seq) =>
        new(
            sessionId: 42,
            sessionVerId: 7,
            msgSeqNum: seq,
            operation,
            new Up.ClOrdID(clOrdId),
            encodedFrameLength: 128,
            encodedFrameSha256: FrameHash);

    private static Up.OutboundAttemptException AttemptFailure(
        Up.OutboundAttemptStage stage,
        Up.OutboundFrameIdentity? frame,
        Exception inner) =>
        new("typed SDK outbound failure", stage, frame, inner);

    private static Order Order(ulong clOrdId) =>
        new(
            clOrdId,
            new EndClientId("client-a"),
            "PETR4",
            12345,
            OrderSide.Buy,
            OrderType.Limit,
            100,
            30m,
            FirmId);

    private static async Task<B3EntryPointClientGateway> BuildGatewayAsync(
        ILogger<B3EntryPointClientGateway>? logger = null,
        Func<Up.NewOrderRequest, Up.OutboundFramePreparedCallback, CancellationToken, Task<Up.OutboundAttemptReceipt>>? submit = null,
        Func<Up.CancelOrderRequest, Up.OutboundFramePreparedCallback, CancellationToken, Task<Up.OutboundAttemptReceipt>>? cancel = null,
        Func<Up.ReplaceOrderRequest, Up.OutboundFramePreparedCallback, CancellationToken, Task<Up.OutboundAttemptReceipt>>? replace = null,
        Func<Up.NewOrderRequest, CancellationToken, Task>? legacySubmit = null,
        Func<Up.CancelOrderRequest, CancellationToken, Task>? legacyCancel = null,
        Func<Up.ReplaceOrderRequest, CancellationToken, Task>? legacyReplace = null,
        Func<Task>? beforeFailClose = null,
        Func<Sdk.ReconnectMode, Func<uint, uint>, CancellationToken, Task<Sdk.ReconnectOutcome>>? reconnect = null,
        Func<CancellationToken, IAsyncEnumerable<Up.EntryPointEvent>>? events = null,
        bool startEventLoop = false)
    {
        var client = new Sdk.EntryPointClient(new Sdk.EntryPointClientOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 1),
            SessionId = 42,
            SessionVerId = 7,
            EnteringFirm = 9,
            Credentials = Sdk.EntryPointClientOptions.AccessKey("0123456789ABCDEF0123456789ABCDEF"),
            TerminateOnDispose = false,
        });
        var gateway = new B3EntryPointClientGateway(
            client,
            FirmId,
            7,
            logger ?? NullLogger<B3EntryPointClientGateway>.Instance,
            new B3EntryPointClientGateway.OutboundTestOverrides
            {
                Submit = legacySubmit,
                Cancel = legacyCancel,
                Replace = legacyReplace,
                SubmitWithReceipt = submit,
                CancelWithReceipt = cancel,
                ReplaceWithReceipt = replace,
                BeforeFailClose = beforeFailClose,
            },
            terminateOnShutdown: false,
            connectedTestHook: startEventLoop ? null : static () => { },
            connectAsyncOverride: static _ => Task.CompletedTask,
            reconnectAsyncOverride: reconnect,
            eventStreamOverride: events);
        await gateway.ConnectAsync(CancellationToken.None);
        return gateway;
    }

    private sealed class LegacyOnlyGateway : IExchangeGateway
    {
        public int SubmitCalls { get; private set; }

        public Task SubmitAsync(Order order, CancellationToken cancellationToken)
        {
            SubmitCalls++;
            return Task.CompletedTask;
        }

        public Task CancelAsync(Order order, ulong newClOrdId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CancelReplaceAsync(
            Order original,
            ulong newClOrdId,
            long newQuantity,
            decimal? newPrice,
            TimeInForce? requestedTimeInForce,
            decimal? requestedStopPrice,
            DateTimeOffset? requestedGoodTillDate,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class CapturingLogger : ILogger<B3EntryPointClientGateway>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
