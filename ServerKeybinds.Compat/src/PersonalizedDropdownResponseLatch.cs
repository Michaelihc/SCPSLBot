namespace ServerKeybinds;

internal enum PersonalizedDropdownResponseKind
{
    Acquisition,
    Change,
    Duplicate,
}

/// <summary>
/// Classifies the client's first value for a sent dropdown separately from later deliberate changes.
/// Consumers may opt into acquisition for staging-only workflows without executing an action.
/// </summary>
internal sealed class PersonalizedDropdownResponseLatch
{
    private bool hasValue;
    private int currentIndex;

    public PersonalizedDropdownResponseKind Observe(int index)
    {
        if (!hasValue)
        {
            hasValue = true;
            currentIndex = index;
            return PersonalizedDropdownResponseKind.Acquisition;
        }

        if (currentIndex == index)
        {
            return PersonalizedDropdownResponseKind.Duplicate;
        }

        currentIndex = index;
        return PersonalizedDropdownResponseKind.Change;
    }
}
