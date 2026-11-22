namespace RelaxKonOS.UI.FileBrowser;

/// <summary>Sorting and extension lookup for remote file names, independent of the local operating system.</summary>
public static class FileName
{
    public static string Extension(string name)
    {
        var index = name.LastIndexOf('.');
        return index < 0 ? string.Empty : name[index..];
    }

    /// <summary>Compares digit runs by length and value without converting them to integers.</summary>
    public static int CompareNaturally(string x, string y)
    {
        var i = 0;
        var j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var a = i;
                var b = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                while (a < i && x[a] == '0') a++;
                while (b < j && y[b] == '0') b++;
                var length = (i - a).CompareTo(j - b);
                if (length != 0) return length;
                var number = x.AsSpan(a, i - a).SequenceCompareTo(y.AsSpan(b, j - b));
                if (number != 0) return number;
            }
            else
            {
                var value = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (value != 0) return value;
                i++;
                j++;
            }
        }
        var remaining = (x.Length - i).CompareTo(y.Length - j);
        return remaining != 0 ? remaining : StringComparer.Ordinal.Compare(x, y);
    }
}
