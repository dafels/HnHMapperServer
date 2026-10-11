using HnHMapperServer.Core.Constants;

namespace HnHMapperServer.Tests;

/// <summary>
/// Generated tenant ids are {icon}-{icon}-NNNN (TenantNameService). Pins the invariant that makes
/// splitting a client token ({tenantId}_{secret}) at the first underscore unambiguous for every
/// generated tenant.
/// </summary>
public class IconNamesTests
{
    [Fact]
    public void Names_ContainNoUnderscore()
    {
        Assert.DoesNotContain(IconNames.Names, n => n.Contains('_'));
    }
}
