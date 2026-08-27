using Microsoft.UI.Xaml;
using BlockFerry.Core.Transactions;

namespace BlockFerry.App.WinUI.Services;

internal interface ICompletionSoundPlayer
{
    void PlayShow();
}

internal sealed class ElementSoundCompletionPlayer : ICompletionSoundPlayer
{
    public void PlayShow() => ElementSoundPlayer.Play(ElementSoundKind.Show);
}

internal sealed class CompletionSoundGate(ICompletionSoundPlayer player)
{
    private readonly object _syncRoot = new();
    private readonly HashSet<TransactionId> _playedTransactions = [];
    private long _acceptedGeneration;

    public void ResetForNewGeneration(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        lock (_syncRoot)
        {
            if (generation < _acceptedGeneration)
            {
                return;
            }

            if (generation != _acceptedGeneration)
            {
                _acceptedGeneration = generation;
                _playedTransactions.Clear();
            }
        }
    }

    public bool TryPlayCommitted(
        long acceptedGeneration,
        TransactionId transactionId,
        bool durableVerifiedCommit,
        bool resultPresented,
        bool focusAccepted,
        bool validAutomationPeer,
        bool notificationInvokedSuccessfully)
    {
        if (!durableVerifiedCommit ||
            !resultPresented ||
            !focusAccepted ||
            !validAutomationPeer ||
            !notificationInvokedSuccessfully ||
            transactionId.Value == Guid.Empty)
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (acceptedGeneration != _acceptedGeneration ||
                !_playedTransactions.Add(transactionId))
            {
                return false;
            }

            player.PlayShow();
            return true;
        }
    }
}
