using Avalonia.Controls;
using Avalonia.Media;
using RelaxKonOS.Protocol.Workspace;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;
using RoyalTerminal.Terminal.Transport.Ssh;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// The in-process terminal control (RoyalTerminal) drawn with Skia inside the Avalonia window.
/// This is the surface the client has always used, and the one every platform can host.
/// </summary>
public sealed class BuiltInTerminalSurface : ITerminalSurface
{
    private const int InitialColumns = 80;
    private const int InitialRows = 24;
    private TerminalSettingsDto _appearance = TerminalSettingsDto.Default;
    private bool _started;
    private bool _cursorApplied;
    private const double MinimumSupportedFontSize = 8;
    private const double MaximumSupportedFontSize = 40;

    private readonly TerminalControl _control;

    public BuiltInTerminalSurface(
        ITerminalTransportFactory transportFactory,
        ISshCredentialProvider credentials,
        ISshHostKeyValidator hostKeys)
    {
        // TerminalTransportFactory is a read-only property, so the custom factory has to arrive
        // through the constructor; a XAML-declared control would keep the default composite factory.
        _control = new TerminalControl(
            new TerminalSessionService(),
            new DefaultTerminalInputAdapter(),
            new DefaultTerminalSelectionService(),
            new DefaultTerminalScrollService(),
            new DefaultVtProcessorFactory(),
            new DefaultPtyFactory(),
            credentials,
            hostKeys,
            transportFactory);

        _control.Focusable = true;
        // These values only initialize the PTY. RoyalTerminal derives the live grid from the
        // arranged host size; it never needs to reduce the configured font to fill the window.
        _control.Columns = InitialColumns;
        _control.Rows = InitialRows;
        _control.ScrollbackLimit = _appearance.ScrollbackLines;
        _control.TerminalFontSize = TerminalSettingsDto.Default.FontSize;
        _control.FontFamilyName = TerminalAppearance.ResolveFontFamily(TerminalSettingsDto.Default.FontFamily);
        _control.ProcessExited += OnProcessExited;
        _control.TitleChanged += OnTitleChanged;
        _control.SelectionFinalized += OnSelectionFinalized;
    }

    public Control View => _control;

    public TerminalControl Control => _control;

    public bool HasSelection => _control.HasSelection;

    public event EventHandler<int>? ProcessExited;
    public event EventHandler<string>? TitleChanged;
    public event EventHandler? SelectionChanged;

    public async Task StartSessionAsync(ITerminalTransportOptions options, CancellationToken cancellationToken)
    {
        await _control.StartSessionAsync(options, cancellationToken);
        _started = true;
        ApplyAppearance(_appearance);
    }

    /// <summary>
    /// The terminal screen is (re)created inside StartSessionAsync, and the ScrollbackLimit set in the
    /// constructor does not always propagate to the freshly built screen. Re-assert it on both the
    /// control and the screen so the user can actually scroll back through the buffer.
    /// </summary>
    private void EnsureScrollback()
    {
        _control.ScrollbackLimit = _appearance.ScrollbackLines;
        if (_control.Screen is { } screen)
            screen.ScrollbackLimit = _appearance.ScrollbackLines;
    }

    public void StopSession()
    {
        _started = false;
        _cursorApplied = false;
        try { _control.StopPty(); } catch { /* best effort */ }
    }

    public void Focus() => _control.Focus();

    public async Task CopySelectionAsync()
    {
        try { await _control.CopySelectionAsync(); } catch { /* clipboard may be unavailable */ }
    }

    public async Task PasteAsync()
    {
        try { await _control.PasteAsync(); } catch { /* clipboard may be unavailable */ }
    }

    public void SelectAll()
    {
        try { _control.SelectAll(); } catch { /* best effort */ }
    }

    public void ClearScreen()
    {
        try
        {
            // ClearScrollback() alone keeps the active viewport intact, so the screen would still
            // look full. ClearHistory() drops the visible rows first, then the history above them.
            _control.ClearHistory();
            _control.ClearScrollback();
        }
        catch { /* best effort */ }
    }

    public void ApplyAppearance(TerminalSettingsDto appearance)
    {
        var cursorChanged = _appearance.CursorStyle != appearance.CursorStyle || _appearance.CursorBlink != appearance.CursorBlink;
        _appearance = appearance;
        EnsureScrollback();
        if (_started && (!_cursorApplied || cursorChanged))
        {
            _control.WriteOutput(System.Text.Encoding.ASCII.GetBytes(CursorSequence(appearance)));
            _cursorApplied = true;
        }
        _control.FontFamilyName = TerminalAppearance.ResolveFontFamily(appearance.FontFamily);
        _control.TerminalFontSize = Math.Clamp(appearance.FontSize, MinimumSupportedFontSize, MaximumSupportedFontSize);

        // Renderer palette APIs differ between RoyalTerminal renderers; apply values when present.
        ApplyColor(_control, "TextColor", appearance.ForegroundColor);
        ApplyColor(_control, "CursorColor", appearance.CursorColor);
        ApplyColor(_control.Theme, "BackgroundColor", appearance.BackgroundColor);
        ApplyColor(_control.Theme, "ForegroundColor", appearance.ForegroundColor);
        ApplyColor(_control.Theme, "CursorColor", appearance.CursorColor);
    }

    internal static string CursorSequence(TerminalSettingsDto appearance)
    {
        var steady = appearance.CursorStyle switch { "underline" => 4, "bar" => 6, _ => 2 };
        return "\u001b[" + (appearance.CursorBlink ? steady - 1 : steady) + " q";
    }

    /// <summary>The built-in control draws its own chrome, so display-language labels do not apply.</summary>
    public void ApplyLabels(IReadOnlyDictionary<string, string> labels) { }

    /// <summary>The built-in control is composed by Avalonia itself and needs no visibility juggling.</summary>
    public void SetNativeVisible(bool visible) { }

    public void Dispose()
    {
        _control.ProcessExited -= OnProcessExited;
        _control.TitleChanged -= OnTitleChanged;
        _control.SelectionFinalized -= OnSelectionFinalized;
    }

    private void OnProcessExited(object? sender, int exitCode) => ProcessExited?.Invoke(this, exitCode);

    private void OnTitleChanged(object? sender, string title) => TitleChanged?.Invoke(this, title);

    private void OnSelectionFinalized(object? sender, EventArgs e) => SelectionChanged?.Invoke(this, EventArgs.Empty);

    private static void ApplyColor(object? target, string propertyName, string value)
    {
        if (target is null || target.GetType().GetProperty(propertyName) is not { CanWrite: true } property)
            return;

        try
        {
            if (property.PropertyType == typeof(Color))
            {
                property.SetValue(target, Color.Parse(value));
                return;
            }

            var parse = property.PropertyType.GetMethod("Parse", [typeof(string)]);
            if (parse is not null)
                property.SetValue(target, parse.Invoke(null, [value]));
        }
        catch { /* unsupported renderer palette value */ }
    }
}
