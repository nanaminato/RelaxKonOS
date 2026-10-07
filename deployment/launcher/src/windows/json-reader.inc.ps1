# --- strict JSON ---------------------------------------------------------------------------------
# A hand-written reader is used instead of ConvertFrom-Json because the launcher must reject
# duplicate keys and control the exact member set; ConvertFrom-Json silently keeps the last
# duplicate and is not available with strict semantics on every PowerShell version we support.
function Skip-JsonWhitespace([string] $Text, [ref] $Index) {
    while ($Index.Value -lt $Text.Length -and [char]::IsWhiteSpace($Text[$Index.Value])) { $Index.Value++ }
}

function Read-JsonString([string] $Text, [ref] $Index) {
    $Index.Value++
    $builder = New-Object Text.StringBuilder
    while ($true) {
        if ($Index.Value -ge $Text.Length) { throw 'Unterminated JSON string.' }
        $character = $Text[$Index.Value]
        if ($character -eq '"') { $Index.Value++; break }
        if ($character -eq '\') {
            $Index.Value++
            if ($Index.Value -ge $Text.Length) { throw 'Unterminated JSON escape.' }
            $escape = $Text[$Index.Value]
            if ($escape -eq '"') { $null = $builder.Append('"') }
            elseif ($escape -eq '\') { $null = $builder.Append('\') }
            elseif ($escape -eq '/') { $null = $builder.Append('/') }
            elseif ($escape -eq 'b') { $null = $builder.Append([char]8) }
            elseif ($escape -eq 'f') { $null = $builder.Append([char]12) }
            elseif ($escape -eq 'n') { $null = $builder.Append([char]10) }
            elseif ($escape -eq 'r') { $null = $builder.Append([char]13) }
            elseif ($escape -eq 't') { $null = $builder.Append([char]9) }
            elseif ($escape -eq 'u') {
                if ($Index.Value + 4 -ge $Text.Length) { throw 'Truncated JSON unicode escape.' }
                $hex = $Text.Substring($Index.Value + 1, 4)
                if ($hex -notmatch '^[0-9a-fA-F]{4}$') { throw 'Invalid JSON unicode escape.' }
                $null = $builder.Append([char][Convert]::ToInt32($hex, 16))
                $Index.Value += 4
            }
            else { throw 'Invalid JSON escape sequence.' }
            $Index.Value++
            continue
        }
        if ([int]$character -lt 32) { throw 'Control character in JSON string.' }
        $null = $builder.Append($character)
        $Index.Value++
    }
    return $builder.ToString()
}

function Read-JsonValue([string] $Text, [ref] $Index) {
    Skip-JsonWhitespace $Text $Index
    if ($Index.Value -ge $Text.Length) { throw 'Unexpected end of JSON input.' }
    $character = $Text[$Index.Value]
    if ($character -eq '{') { return (Read-JsonObject $Text $Index) }
    if ($character -eq '[') { return (Read-JsonArray $Text $Index) }
    if ($character -eq '"') { return (Read-JsonString $Text $Index) }
    $remaining = $Text.Substring($Index.Value)
    foreach ($literal in @('true', 'false', 'null')) {
        if ($remaining.StartsWith($literal, [StringComparison]::Ordinal)) {
            $Index.Value += $literal.Length
            if ($literal -eq 'true') { return $true }
            if ($literal -eq 'false') { return $false }
            return $null
        }
    }
    $match = [regex]::Match($remaining, '^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?')
    if (-not $match.Success) { throw 'Invalid JSON value.' }
    $Index.Value += $match.Length
    if ($match.Value -match '[.eE]') { return [double]$match.Value }
    return [long]$match.Value
}

function Read-JsonObject([string] $Text, [ref] $Index) {
    $Index.Value++
    $members = [ordered]@{}
    Skip-JsonWhitespace $Text $Index
    if ($Index.Value -lt $Text.Length -and $Text[$Index.Value] -eq '}') { $Index.Value++; return $members }
    while ($true) {
        Skip-JsonWhitespace $Text $Index
        if ($Index.Value -ge $Text.Length -or $Text[$Index.Value] -ne '"') { throw 'Expected a JSON object key.' }
        $key = Read-JsonString $Text $Index
        if ($members.Contains($key)) { throw "Duplicate JSON key: $key" }
        Skip-JsonWhitespace $Text $Index
        if ($Index.Value -ge $Text.Length -or $Text[$Index.Value] -ne ':') { throw 'Expected a colon after a JSON object key.' }
        $Index.Value++
        $members[$key] = Read-JsonValue $Text $Index
        Skip-JsonWhitespace $Text $Index
        if ($Index.Value -ge $Text.Length) { throw 'Unterminated JSON object.' }
        if ($Text[$Index.Value] -eq ',') { $Index.Value++; continue }
        if ($Text[$Index.Value] -eq '}') { $Index.Value++; break }
        throw 'Expected a comma or closing brace in a JSON object.'
    }
    return $members
}

function Read-JsonArray([string] $Text, [ref] $Index) {
    $Index.Value++
    $items = New-Object System.Collections.ArrayList
    Skip-JsonWhitespace $Text $Index
    if ($Index.Value -lt $Text.Length -and $Text[$Index.Value] -eq ']') { $Index.Value++; return , $items }
    while ($true) {
        $null = $items.Add((Read-JsonValue $Text $Index))
        Skip-JsonWhitespace $Text $Index
        if ($Index.Value -ge $Text.Length) { throw 'Unterminated JSON array.' }
        if ($Text[$Index.Value] -eq ',') { $Index.Value++; continue }
        if ($Text[$Index.Value] -eq ']') { $Index.Value++; break }
        throw 'Expected a comma or closing bracket in a JSON array.'
    }
    return , $items
}

function ConvertFrom-StrictJsonObject([string] $Text) {
    $index = 0
    $value = Read-JsonValue $Text ([ref]$index)
    Skip-JsonWhitespace $Text ([ref]$index)
    if ($index -ne $Text.Length) { throw 'Trailing content after the JSON value.' }
    if ($value -isnot [Collections.IDictionary]) { throw 'The request must be a JSON object.' }
    return $value
}

