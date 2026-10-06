using AssumptionZero.Domain.Entities;

namespace AssumptionZero.Application.Services;

/// <summary>Single-writer, account-wide balances and accepted but unacknowledged intents.</summary>
public sealed class AccountRiskLedger(string[] assets)
{
    private readonly decimal[] free = new decimal[assets.Length];
    private readonly decimal[] locked = new decimal[assets.Length];
    private readonly decimal[] reserved = new decimal[assets.Length];
    private readonly Intent[] intents = new Intent[4_096];
    private long nextIntentId;
    public int AssetCount => assets.Length;
    public string[] Assets => (string[])assets.Clone();
    public decimal RawFree(int asset) => free[asset];
    public decimal Locked(int asset) => locked[asset];
    public decimal Reserved(int asset) => reserved[asset];
    public int PendingCount => intents.Count(i => i.Id != 0);
    public bool Ready { get; private set; }
    public decimal MakerFeeRate { get; private set; }

    private struct Intent
    {
        public long Id;
        public int Asset;
        public decimal Amount;
    }

    public int FindAsset(ReadOnlySpan<char> asset)
    {
        for (int i = 0; i < assets.Length; i++)
            if (asset.SequenceEqual(assets[i])) return i;
        return -1;
    }

    public int FindAsset(ReadOnlySpan<byte> utf8Asset)
    {
        for (int i = 0; i < assets.Length; i++)
        {
            ReadOnlySpan<char> name = assets[i];
            if (name.Length != utf8Asset.Length) continue;
            bool equal = true;
            for (int j = 0; j < name.Length; j++)
                if (name[j] != utf8Asset[j]) { equal = false; break; }
            if (equal) return i;
        }
        return -1;
    }

    public void BeginSnapshot(decimal makerFeeRate)
    {
        if (makerFeeRate < 0) throw new ArgumentOutOfRangeException(nameof(makerFeeRate));
        Ready = false;
        MakerFeeRate = makerFeeRate;
        Array.Clear(free);
        Array.Clear(locked);
        Array.Clear(reserved);
        Array.Clear(intents);
    }

    public void SetBalance(int asset, decimal available, decimal held)
    {
        if ((uint)asset >= (uint)assets.Length || available < 0 || held < 0)
        {
            Ready = false;
            return;
        }
        free[asset] = available;
        locked[asset] = held;
    }

    public void CompleteSnapshot() => Ready = true;
    public void Invalidate() => Ready = false;
    public decimal Free(int asset) => (uint)asset < (uint)assets.Length ? Math.Max(0, free[asset] - reserved[asset]) : 0;
    public decimal Total(int asset) => (uint)asset < (uint)assets.Length ? free[asset] + locked[asset] : 0;

    public bool TryAccept(Instrument instrument, bool buy, long priceTicks, long quantity, out long intentId)
        => Reserve(instrument, buy, priceTicks, quantity, true, out intentId);

    /// <summary>Keep an existing order's funds held until cancellation is confirmed.</summary>
    public bool ReservePendingCancel(Instrument instrument, bool buy, long priceTicks, long quantity,
        out long intentId)
        => Reserve(instrument, buy, priceTicks, quantity, false, out intentId);

    private bool Reserve(Instrument instrument, bool buy, long priceTicks, long quantity,
        bool requireAvailable, out long intentId)
    {
        intentId = 0;
        if (!Ready || quantity <= 0 || priceTicks <= 0) return false;
        int asset = FindAsset(buy ? instrument.QuoteAsset : instrument.BaseAsset);
        if (asset < 0) return false;
        decimal amount = instrument.Quantity(quantity) * (buy ? instrument.Price(priceTicks) * (1 + MakerFeeRate) : 1);
        if (requireAvailable && amount > Free(asset)) return false;
        for (int i = 0; i < intents.Length; i++)
        {
            if (intents[i].Id != 0) continue;
            long id = ++nextIntentId;
            intents[i] = new Intent { Id = id, Asset = asset, Amount = amount };
            reserved[asset] += amount;
            intentId = id;
            return true;
        }
        Invalidate();
        return false;
    }

    public bool Release(long intentId)
    {
        for (int i = 0; i < intents.Length; i++)
        {
            if (intents[i].Id != intentId) continue;
            reserved[intents[i].Asset] -= intents[i].Amount;
            intents[i] = default;
            return true;
        }
        return false;
    }
}
