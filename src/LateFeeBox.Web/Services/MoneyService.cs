using LateFeeBox.Web.Options;
using Microsoft.Extensions.Options;

namespace LateFeeBox.Web.Services;

public sealed class MoneyService(IOptions<MoneyOptions> options)
{
    private readonly bool _displayInTomans = options.Value.DisplayInTomans;
    public string UnitName => _displayInTomans ? "تومان" : "ریال";
    public long ToRials(long displayAmount) => _displayInTomans ? checked(displayAmount * 10) : displayAmount;
    public long ToDisplayUnits(long rials) => _displayInTomans ? rials / 10 : rials;
    public string Format(long rials) => $"{ToDisplayUnits(rials):N0} {UnitName}";
}
