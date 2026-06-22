using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class MsgView : ReactiveUserControl<MsgViewModel>
{
    //private const int KeepLines = 30;
    private const double ScrollBottomTolerance = 1.0;
    private bool _autoScrollToEnd = true;
    private bool _ignoreScrollOffsetChange;

    public MsgView()
    {
        InitializeComponent();
        txtMsg.TextArea.TextView.Options.EnableHyperlinks = false;
        ViewModel = new MsgViewModel(UpdateViewHandler);
        DetachedFromVisualTree += (_, _) => ViewModel?.Dispose();

        this.WhenActivated(disposables =>
        {
            this.Bind(ViewModel, vm => vm.MsgFilter, v => v.cmbMsgFilter.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.AutoRefresh, v => v.togAutoRefresh.IsChecked).DisposeWith(disposables);
        });

        TextEditorKeywordHighlighter.Attach(txtMsg, Global.LogLevelColors.ToDictionary(
                kv => kv.Key,
                kv => (IBrush)new SolidColorBrush(Color.Parse(kv.Value))
            ));
        txtMsg.TextArea.TextView.ScrollOffsetChanged += TextView_ScrollOffsetChanged;
    }

    private async Task<bool> UpdateViewHandler(EViewAction action, object? obj)
    {
        switch (action)
        {
            case EViewAction.DispatcherShowMsg:
                if (obj is null)
                {
                    return false;
                }

                Dispatcher.UIThread.Post(() => ShowMsg(obj),
                    DispatcherPriority.ApplicationIdle);
                break;

            case EViewAction.DispatcherScrollToEnd:
                Dispatcher.UIThread.Post(ScrollToEnd,
                    DispatcherPriority.ApplicationIdle);
                break;
        }
        return await Task.FromResult(true);
    }

    private void ShowMsg(object msg)
    {
        //var lineCount = txtMsg.LineCount;
        //if (lineCount > ViewModel?.NumMaxMsg)
        //{
        //    var cutLine = txtMsg.Document.GetLineByNumber(lineCount - KeepLines);
        //    txtMsg.Document.Remove(0, cutLine.Offset);
        //}
        if (txtMsg.LineCount > ViewModel?.NumMaxMsg)
        {
            ClearMsg();
        }

        IgnoreScrollOffsetChange();
        txtMsg.AppendText(msg.ToString());
    }

    public void ClearMsg()
    {
        txtMsg.Clear();
        txtMsg.AppendText("----- Message cleared -----\n");
        _autoScrollToEnd = true;
    }

    private void TextView_ScrollOffsetChanged(object? sender, EventArgs e)
    {
        if (_ignoreScrollOffsetChange)
        {
            return;
        }

        _autoScrollToEnd = IsScrolledToEnd();
    }

    private bool CanScrollToEnd()
    {
        return (togScrollToEnd.IsChecked ?? true) && (_autoScrollToEnd || IsScrolledToEnd());
    }

    private bool IsScrolledToEnd()
    {
        var textView = txtMsg.TextArea.TextView;
        if (textView is null)
        {
            return true;
        }

        var maxOffset = Math.Max(0, textView.DocumentHeight - textView.Bounds.Height);
        return textView.ScrollOffset.Y >= maxOffset - ScrollBottomTolerance;
    }

    private void ScrollToEnd()
    {
        if (!CanScrollToEnd())
        {
            return;
        }

        IgnoreScrollOffsetChange();
        txtMsg.ScrollToEnd();
        _autoScrollToEnd = true;
    }

    private void IgnoreScrollOffsetChange()
    {
        _ignoreScrollOffsetChange = true;
        Dispatcher.UIThread.Post(() => _ignoreScrollOffsetChange = false, DispatcherPriority.Background);
    }

    private void menuMsgViewSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            txtMsg.TextArea.Focus();
            txtMsg.SelectAll();
        }, DispatcherPriority.Render);
    }

    private async void menuMsgViewCopy_Click(object? sender, RoutedEventArgs e)
    {
        var data = txtMsg.SelectedText.TrimEx();
        await AvaUtils.SetClipboardData(this, data);
    }

    private async void menuMsgViewCopyAll_Click(object? sender, RoutedEventArgs e)
    {
        var data = txtMsg.Text.TrimEx();
        await AvaUtils.SetClipboardData(this, data);
    }

    private void menuMsgViewClear_Click(object? sender, RoutedEventArgs e)
    {
        ClearMsg();
    }
}
