using System.Net;
using Jularr.Frontend.Framework;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class LanDiscoveryTests
    {
        [DataTestMethod]
        [DataRow("10.1.2.3", true)]
        [DataRow("172.16.0.1", true)]
        [DataRow("172.31.255.254", true)]
        [DataRow("192.168.1.10", true)]
        [DataRow("169.254.1.1", true)]
        [DataRow("127.0.0.1", true)]
        [DataRow("172.32.1.1", false)]
        [DataRow("172.15.1.1", false)]
        [DataRow("8.8.8.8", false)]
        [DataRow("1.1.1.1", false)]
        [DataRow("::1", false)]
        public void OnlyPrivateIpv4NetworksAreEligibleForDiscovery(string address, bool expected)
        {
            Assert.AreEqual(expected, LanDiscoveryService.IsPrivateLanAddress(IPAddress.Parse(address)));
        }
    }
}
