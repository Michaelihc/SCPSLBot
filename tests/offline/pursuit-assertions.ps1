# Native event evidence only; never count bot diagnostics as a successful interaction or ability.
function Test-PursuitEvents($Case, [string[]]$Lines, [int]$HumanId, [int]$ScpId) {
    $native = @($Lines | Where-Object { $_ -match '\[ScpAbilityEvent\] event=' })
    $closes = @($native | Where-Object {
        $_ -match "event=door\.Interacted id=$HumanId " -and $_ -match 'canOpen=True targetOpen=False(?: |$)'
    })
    if ($Case.closures -eq 'required' -and !$closes.Count) {
        "$($Case.name): no native human door closure"
    }
    if ($Case.closures -eq 'forbidden' -and $closes.Count) {
        "$($Case.name): unexpected native human door closure ($($closes.Count))"
    }
    if ($Case.keycard -and !@($closes | Where-Object { $_ -match 'keycardRequired=True item=Keycard' }).Count) {
        "$($Case.name): no native keycard-authorized closure"
    }
    foreach ($ability in $Case.abilities) {
        $pattern = 'event=' + [regex]::Escape($ability) + " id=$ScpId "
        $uses = @($native | Where-Object { $_ -match $pattern })
        if ($ability -eq '106.StalkMode') {
            $uses = @($uses | Where-Object { $_ -match ' active=True(?: |$)' })
        }
        if (!$uses.Count) { "$($Case.name): missing native $ability activation" }
    }
}
