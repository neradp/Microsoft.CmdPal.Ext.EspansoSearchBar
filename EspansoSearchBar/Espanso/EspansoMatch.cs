// Copyright (c) the EspansoSearchBar project contributors.
// Licensed under the MIT license. See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace EspansoSearchBar.Espanso;

/// <summary>
/// Represents a single espanso match, as emitted by "espanso match list -j".
///
/// This mirrors the exact JSON shape produced by espanso's own CLI (see
/// JsonMatchEntry in espanso/src/cli/match_cli/list.rs upstream):
/// <code>
/// [
///   { "triggers": [":sig"], "replace": "Best regards,\nJohn", "label": null }
/// ]
/// </code>
/// Matches without a text trigger (e.g. pure regex causes) report the literal
/// string "(none)" as their only trigger; we filter those out when listing.
/// </summary>
public sealed class EspansoMatch
{
    [JsonPropertyName("triggers")]
    public List<string> Triggers { get; set; } = [];

    [JsonPropertyName("replace")]
    public string Replace { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>Primary trigger used to invoke this match ("espanso match exec -t &lt;trigger&gt;").</summary>
    public string PrimaryTrigger => Triggers.Count > 0 ? Triggers[0] : string.Empty;

    /// <summary>Human friendly text shown as the list item subtitle.</summary>
    public string DisplayReplacement =>
        Replace.Length > 140 ? string.Concat(Replace.AsSpan(0, 140), "…") : Replace;
}
