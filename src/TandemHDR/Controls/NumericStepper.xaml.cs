using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
// WinForms and WPF are both enabled in this project and share several type names
// (UserControl, KeyEventArgs, ...); alias the WPF ones explicitly rather than rely on
// using-directive precedence.
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TandemHdr.Controls;

/// <summary>A bounded integer input with step buttons, in the shape of Fluent's
/// NumberBox. WPF ships no numeric up/down control.</summary>
internal partial class NumericStepper : UserControl
{
    private static readonly Regex DigitsOnly = new("^[0-9]*$");

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumericStepper),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged, CoerceValue));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(NumericStepper), new PropertyMetadata(0));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(NumericStepper), new PropertyMetadata(int.MaxValue));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(int), typeof(NumericStepper), new PropertyMetadata(1));

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Step
    {
        get => (int)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public event EventHandler? ValueCommitted;

    public NumericStepper()
    {
        InitializeComponent();
        Loaded += (_, _) => SyncTextFromValue();
    }

    private static object CoerceValue(DependencyObject d, object baseValue)
    {
        var c = (NumericStepper)d;
        int v = (int)baseValue;
        return Math.Clamp(v, c.Minimum, c.Maximum);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NumericStepper)d).SyncTextFromValue();

    private void SyncTextFromValue()
    {
        if (ValueBox.Text != Value.ToString())
            ValueBox.Text = Value.ToString();
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !DigitsOnly.IsMatch(e.Text);

    private void OnValueBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Up) { Step1(+1); e.Handled = true; }
        else if (e.Key == Key.Down) { Step1(-1); e.Handled = true; }
    }

    private void OnValueBoxLostFocus(object sender, RoutedEventArgs e) => CommitText();

    private void CommitText()
    {
        if (int.TryParse(ValueBox.Text, out int parsed))
            Value = parsed; // CoerceValue clamps
        SyncTextFromValue();
        ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void OnUpClick(object sender, RoutedEventArgs e) => Step1(+1);
    private void OnDownClick(object sender, RoutedEventArgs e) => Step1(-1);

    private void Step1(int direction)
    {
        Value += direction * Step;
        ValueCommitted?.Invoke(this, EventArgs.Empty);
    }
}
