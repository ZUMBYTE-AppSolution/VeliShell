namespace VeliShell.Core;

public static class PinOrder
{
    /// <summary>
    /// Moves the pin with <paramref name="id"/> to a slot in the original list.
    /// Slot zero is before the first item and <c>pins.Count</c> is after the last item.
    /// </summary>
    /// <returns><see langword="true"/> when the list order changed.</returns>
    public static bool MoveToInsertionIndex(IList<Pin> pins, string id, int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(id);

        var sourceIndex = -1;
        for (var index = 0; index < pins.Count; index++)
        {
            if (string.Equals(pins[index].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                sourceIndex = index;
                break;
            }
        }

        if (sourceIndex < 0) return false;

        var targetIndex = Math.Clamp(insertionIndex, 0, pins.Count);
        if (sourceIndex < targetIndex) targetIndex--;
        if (sourceIndex == targetIndex) return false;

        var pin = pins[sourceIndex];
        pins.RemoveAt(sourceIndex);
        pins.Insert(targetIndex, pin);
        return true;
    }
}
