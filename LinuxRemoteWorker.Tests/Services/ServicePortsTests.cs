using LinuxRemoteWorker.Modules.Services;

namespace LinuxRemoteWorker.Tests.Services;

public class ServicePortsTests
{
    [Theory]
    [InlineData("http://0.0.0.0:5000", new[] { 5000 })]
    [InlineData("http://0.0.0.0:5000;https://*:5001", new[] { 5000, 5001 })]
    [InlineData(" http://127.0.0.1:5000 ; http://localhost:5000 ", new[] { 5000 })]
    [InlineData("http://[::]:8080", new[] { 8080 })]
    [InlineData("http://+:5002", new[] { 5002 })]
    [InlineData("http://localhost", new[] { 80 })]
    [InlineData("HTTPS://example.com", new[] { 443 })]
    [InlineData("garbage", new int[0])]
    [InlineData("", new int[0])]
    public void Ports_are_read_from_ASPNETCORE_URLS(string urls, int[] expected)
    {
        Assert.Equal(expected, ServicePorts.Parse(urls));
    }

    [Fact]
    public void Ports_per_service_are_read_from_the_units()
    {
        var grep = string.Join("\n",
            "/etc/systemd/system/lrw-shop-prod.service:Environment=ASPNETCORE_URLS=http://127.0.0.1:5000",
            "/etc/systemd/system/lrw-shop-dev.service:Environment=\"ASPNETCORE_URLS=http://127.0.0.1:5001;http://127.0.0.1:5002\"\r",
            "/etc/systemd/system/lrw-shop-dev.service:Environment=ASPNETCORE_URLS=http://127.0.0.1:5001",
            "/etc/systemd/system/other.service:Environment=ASPNETCORE_URLS=http://127.0.0.1:7000",
            "grep: /etc/systemd/system/lrw-*.service: No such file or directory");

        var units = ServicePorts.FromUnits(grep);

        Assert.Equal(2, units.Count);
        Assert.Equal([5000], units["shop-prod"]);
        Assert.Equal([5001, 5002], units["shop-dev"]);
    }

    [Fact]
    public void Listening_ports_are_read_from_ss()
    {
        var listening = ServicePorts.Listening("0.0.0.0:5000\n[::]:22\n*:80\n127.0.0.1:5432\n[::ffff:127.0.0.1]:8080\nnoise\n");

        Assert.Equal(new HashSet<int> { 5000, 22, 80, 5432, 8080 }, listening);
    }

    private static readonly Dictionary<string, List<int>> Units = new()
    {
        ["shop-prod"] = [5000],
        ["shop-dev"] = [5001]
    };

    [Fact]
    public void A_second_instance_on_the_first_ones_port_is_refused()
    {
        var conflicts = ServicePorts.Conflicts("shop-staging", [5000], Units, [5000, 5001, 22]);

        Assert.Equal(["port 5000 is already used by service shop-prod"], conflicts);
    }

    [Fact]
    public void Redeploying_a_service_on_its_own_port_is_fine()
    {
        Assert.Empty(ServicePorts.Conflicts("shop-prod", [5000], Units, [5000, 5001]));
    }

    [Fact]
    public void A_port_some_other_process_listens_on_is_refused()
    {
        var conflicts = ServicePorts.Conflicts("shop-staging", [5432], Units, [5432]);

        Assert.Equal(["port 5432 is already in use on the server (see `ss -tlnp`)"], conflicts);
    }

    [Fact]
    public void Free_ports_pass()
    {
        Assert.Empty(ServicePorts.Conflicts("shop-staging", [5002], Units, [5000, 5001]));
    }

    [Fact]
    public void The_suggested_port_is_the_first_free_one_from_5000()
    {
        Assert.Equal(5003, ServicePorts.SuggestFree(Units, [5002, 22]));
        Assert.Equal(5000, ServicePorts.SuggestFree([], []));
    }
}
