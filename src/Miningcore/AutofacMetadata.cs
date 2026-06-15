using Miningcore.Configuration;

namespace Miningcore;

public class CoinFamilyAttribute : Attribute
{
    public CoinFamilyAttribute(IDictionary<string, object> values)
    {
        if(values.TryGetValue(nameof(SupportedFamilies), out var families))
            SupportedFamilies = (CoinFamily[]) families;
    }

    public CoinFamilyAttribute(params CoinFamily[] supportedFamilies)
    {
        SupportedFamilies = supportedFamilies;
    }

    public CoinFamily[] SupportedFamilies { get; }
}

public class IdentifierAttribute : Attribute
{
    public IdentifierAttribute(string name)
    {
        Name = name;
    }

    public string Name { get; }
}
