using System.Text;

namespace UndoTheSpire2;

internal sealed class UndoChoiceResultKey : IEquatable<UndoChoiceResultKey>
{
    public UndoChoiceResultKey(IEnumerable<int> optionIndexes)
    {
        OptionIndexes = [.. optionIndexes];
    }

    public IReadOnlyList<int> OptionIndexes { get; }

    public bool Equals(UndoChoiceResultKey? other)
    {
        return other != null && OptionIndexes.SequenceEqual(other.OptionIndexes);
    }

    public override bool Equals(object? obj)
    {
        return obj is UndoChoiceResultKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (int optionIndex in OptionIndexes)
            hash.Add(optionIndex);
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        if (OptionIndexes.Count == 0)
            return "empty";

        StringBuilder builder = new();
        for (int i = 0; i < OptionIndexes.Count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(OptionIndexes[i]);
        }

        return builder.ToString();
    }
}