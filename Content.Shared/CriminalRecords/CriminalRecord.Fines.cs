// iss14: Economy — fines attached to criminal records.
using Robust.Shared.Serialization;

namespace Content.Shared.CriminalRecords;

public sealed partial record CriminalRecord
{
    /// <summary>Fines issued to this person. Paid ones stay for the history.</summary>
    [DataField]
    public List<CriminalFine> Fines = new();

    public int OpenFineTotal
    {
        get
        {
            var total = 0;
            foreach (var fine in Fines)
            {
                if (!fine.Paid)
                    total += fine.Amount;
            }
            return total;
        }
    }
}

/// <summary>One fine. <see cref="Amount"/> shrinks when it is partly settled from the salary.</summary>
[Serializable, NetSerializable, DataDefinition]
public sealed partial class CriminalFine
{
    [DataField]
    public uint Id;

    [DataField]
    public int Amount;

    /// <summary>What was originally issued, for the history.</summary>
    [DataField]
    public int OriginalAmount;

    [DataField]
    public string Reason = string.Empty;

    [DataField]
    public string IssuerName = string.Empty;

    /// <summary>Round time when the fine was issued.</summary>
    [DataField]
    public TimeSpan IssuedAt;

    [DataField]
    public bool Paid;

    /// <summary>Who settled it: the person, their lawyer, or "salary".</summary>
    [DataField]
    public string? PaidBy;
}
