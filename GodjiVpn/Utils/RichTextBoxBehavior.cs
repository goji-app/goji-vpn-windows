using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace GodjiVpn.Utils;

/// <summary>
/// RichTextBox.Document — обычное CLR-свойство, а не DependencyProperty, поэтому
/// Document="{Binding ...}" в XAML падает рантайм-исключением ("Binding можно задать только
/// в параметре DependencyProperty"). Attached property — стандартный WPF-обход: сама
/// AttachedDocument уже настоящий DependencyProperty и поддерживает Binding, а в callback
/// просто прокидывает значение в реальный RichTextBox.Document.
/// </summary>
public static class RichTextBoxBehavior
{
    public static readonly DependencyProperty AttachedDocumentProperty =
        DependencyProperty.RegisterAttached("AttachedDocument", typeof(FlowDocument), typeof(RichTextBoxBehavior),
            new PropertyMetadata(null, OnAttachedDocumentChanged));

    public static void SetAttachedDocument(RichTextBox box, FlowDocument? value) => box.SetValue(AttachedDocumentProperty, value);
    public static FlowDocument? GetAttachedDocument(RichTextBox box) => (FlowDocument?)box.GetValue(AttachedDocumentProperty);

    private static void OnAttachedDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RichTextBox box) box.Document = (FlowDocument?)e.NewValue ?? new FlowDocument();
    }
}
