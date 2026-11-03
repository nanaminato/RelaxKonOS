using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Firewall;

namespace RelaxKonOS.Client.Apps.Firewall;

/// <summary>
/// One selectable firewall value (policy, action, direction or protocol). The label is resolved on
/// every read instead of being cached in the instance, so a display-language switch reaches the
/// combo boxes without rebuilding the list or losing the current selection.
/// </summary>
public sealed class FirewallOption : LocalizedObservableObject
{
    private readonly string _labelKey;

    public FirewallOption(string value, string labelKey)
    {
        Value = value;
        _labelKey = labelKey;
    }

    /// <summary>The structured token the Server and Helper validate; never a prompt string.</summary>
    public string Value { get; }

    public string Label => LocalizedText.Get(_labelKey, _labelKey);

    public override string ToString() => Label;
}

/// <summary>
/// A host rule as the rule table needs it: the structured DTO stays untouched for writes, while
/// every column reads a localized label instead of exposing UFW tokens such as <c>in</c> or
/// <c>any</c> to the user.
/// </summary>
public sealed class FirewallRuleRow : LocalizedObservableObject
{
    public FirewallRuleRow(FirewallRuleDto rule) => Rule = rule;

    public FirewallRuleDto Rule { get; }

    public int Number => Rule.Number;

    public string ActionLabel => Token("firewall.choice.", Rule.Action);

    /// <summary>Tone flags for the action tag: allow must not read like deny, and reject shares deny's tone.</summary>
    public bool ActionAllow => Rule.Action.Equals("allow", StringComparison.OrdinalIgnoreCase);
    public bool ActionDeny => Rule.Action.Equals("deny", StringComparison.OrdinalIgnoreCase)
        || Rule.Action.Equals("reject", StringComparison.OrdinalIgnoreCase);
    public bool ActionLimit => Rule.Action.Equals("limit", StringComparison.OrdinalIgnoreCase);

    public string DirectionLabel => Token("firewall.choice.", Rule.Direction);

    public string ProtocolLabel => Token("firewall.choice.", Rule.Protocol);

    public string AddressFamilyLabel => Rule.AddressFamily;

    public string SourceLabel => Address(Rule.Source);

    public string DestinationLabel => Address(Rule.Destination);

    public string PortLabel => IsAny(Rule.Port) ? Any : Rule.Port;

    /// <summary>One sentence that reads the whole rule back, used by the detail panel and confirmations.</summary>
    public string Sentence => LocalizedText.Format("firewall.sentence.rule",
        ActionLabel, DirectionLabel, ProtocolLabel,
        string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizedText.Get("firewall.sentence.port", "port {0}"),
            IsAny(Rule.Port) ? Any : Rule.Port),
        string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizedText.Get("firewall.sentence.from", "from {0}"), SourceLabel),
        string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizedText.Get("firewall.sentence.to", "to {0}"), DestinationLabel));

    /// <summary>Matches the free-text filter against the values the user sees and the host tokens.</summary>
    public bool Matches(string filter) =>
        Contains(Number.ToString(System.Globalization.CultureInfo.InvariantCulture), filter)
        || Contains(Rule.Action, filter) || Contains(Rule.Direction, filter) || Contains(Rule.Protocol, filter)
        || Contains(Rule.AddressFamily, filter) || Contains(Rule.Source, filter)
        || Contains(Rule.Destination, filter) || Contains(Rule.Port, filter)
        || Contains(ActionLabel, filter) || Contains(DirectionLabel, filter) || Contains(ProtocolLabel, filter)
        || Contains(SourceLabel, filter) || Contains(DestinationLabel, filter) || Contains(PortLabel, filter);

    private static string Any => LocalizedText.Get("firewall.value.any", "Any");

    private static bool Contains(string? value, string filter) =>
        value?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true;

    private static bool IsAny(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("any", StringComparison.OrdinalIgnoreCase)
        || value.Equals("anywhere", StringComparison.OrdinalIgnoreCase);

    private static string Address(string? value) => IsAny(value) ? Any : value!;

    /// <summary>Resolves a token through its resource key and keeps the raw token when the host reports
    /// a value this client has no wording for.</summary>
    private static string Token(string prefix, string value)
    {
        var text = LocalizedText.Get(prefix + value.ToLowerInvariant(), "");
        return string.IsNullOrWhiteSpace(text) ? value : text;
    }
}
