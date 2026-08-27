using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace BlockFerry.App.WinUI.Controls;

public sealed class ExpandCollapseButton : Button
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded),
        typeof(bool),
        typeof(ExpandCollapseButton),
        new PropertyMetadata(false, OnIsExpandedChanged));

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    internal event EventHandler? ExpandedStateChanged;

    public ExpandCollapseButton()
    {
        Click += ExpandCollapseButton_Click;
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ExpandCollapseButtonAutomationPeer(this);

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key == VirtualKey.Right)
        {
            IsExpanded = true;
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Left)
        {
            IsExpanded = false;
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private static void OnIsExpandedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var button = (ExpandCollapseButton)dependencyObject;
        var oldState = (bool)args.OldValue
            ? ExpandCollapseState.Expanded
            : ExpandCollapseState.Collapsed;
        var newState = (bool)args.NewValue
            ? ExpandCollapseState.Expanded
            : ExpandCollapseState.Collapsed;

        if (FrameworkElementAutomationPeer.FromElement(button) is ExpandCollapseButtonAutomationPeer peer)
        {
            peer.RaiseExpandCollapseStateChanged(oldState, newState);
        }

        button.ExpandedStateChanged?.Invoke(button, EventArgs.Empty);
    }

    private void ExpandCollapseButton_Click(object sender, RoutedEventArgs e) =>
        IsExpanded = !IsExpanded;
}

internal sealed class ExpandCollapseButtonAutomationPeer(ExpandCollapseButton owner)
    : ButtonAutomationPeer(owner), IExpandCollapseProvider
{
    private ExpandCollapseButton OwnerButton { get; } = owner;

    public ExpandCollapseState ExpandCollapseState => OwnerButton.IsExpanded
        ? ExpandCollapseState.Expanded
        : ExpandCollapseState.Collapsed;

    public void Collapse() => OwnerButton.IsExpanded = false;

    public void Expand() => OwnerButton.IsExpanded = true;

    protected override object? GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.ExpandCollapse
            ? this
            : base.GetPatternCore(patternInterface);

    internal void RaiseExpandCollapseStateChanged(
        ExpandCollapseState oldState,
        ExpandCollapseState newState) =>
        RaisePropertyChangedEvent(
            ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
            oldState,
            newState);
}
