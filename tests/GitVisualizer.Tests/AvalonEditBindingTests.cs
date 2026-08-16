using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using GitVisualizer.App.Controls;
using ICSharpCode.AvalonEdit;

namespace GitVisualizer.Tests;

public sealed class AvalonEditBindingTests
{
    [Fact]
    public void TextBinding_TracksSourceAfterEditingInitiallyEmptyDocument()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var source = new TextSource();
                var editor = new TextEditor();
                BindingOperations.SetBinding(
                    editor,
                    AvalonEditBinding.TextProperty,
                    new Binding(nameof(TextSource.Text))
                    {
                        Source = source,
                        Mode = BindingMode.TwoWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                    });

                Assert.Equal(string.Empty, editor.Text);
                editor.Text = "first edit";
                Assert.Equal("first edit", source.Text);
                Assert.True(BindingOperations.IsDataBound(editor, AvalonEditBinding.TextProperty));

                editor.Text = "second edit";
                Assert.Equal("second edit", source.Text);
                Assert.True(BindingOperations.IsDataBound(editor, AvalonEditBinding.TextProperty));

                source.Text = string.Empty;
                Assert.Equal(string.Empty, editor.Text);
                Assert.True(BindingOperations.IsDataBound(editor, AvalonEditBinding.TextProperty));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    private sealed class TextSource : INotifyPropertyChanged
    {
        private string text = string.Empty;

        public string Text
        {
            get => text;
            set
            {
                if (text == value)
                {
                    return;
                }
                text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
