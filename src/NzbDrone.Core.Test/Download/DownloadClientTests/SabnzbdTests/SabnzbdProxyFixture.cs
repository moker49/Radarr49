using System;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.Sabnzbd;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.DownloadClientTests.SabnzbdTests
{
    [TestFixture]
    public class SabnzbdProxyFixture : CoreTest<SabnzbdProxy>
    {
        private SabnzbdSettings _settings;

        [SetUp]
        public void Setup()
        {
            _settings = new SabnzbdSettings { Host = "localhost", Port = 6789, MovieCategory = "movies", ApiKey = "test-key" };
        }

        private void WithResponse(string content)
        {
            Mocker.GetMock<IHttpClient>()
                .Setup(c => c.Execute(It.IsAny<HttpRequest>()))
                .Returns<HttpRequest>(request => new HttpResponse(request, new HttpHeader(), content));
        }

        [TestCase("{\"status\":false}")]
        [TestCase("{\"status\":false,\"error\":\"\",\"nzo_ids\":[]}")]
        public void duplicate_add_rejection_should_reach_client_adapter_without_generic_error(string content)
        {
            WithResponse(content);

            var response = Subject.DownloadNzb(new byte[] { 1 }, "movie.nzb", "movies", 0, _settings);

            Assert.That(response.Status, Is.False);
            Assert.That(response.Ids, Is.Empty);
        }

        [Test]
        public void explicit_add_error_should_retain_existing_error_handling()
        {
            WithResponse("{\"status\":false,\"error\":\"API key incorrect\"}");

            Assert.Throws<DownloadClientException>(() => Subject.DownloadNzb(new byte[] { 1 }, "movie.nzb", "movies", 0, _settings));
        }

        [Test]
        public void empty_error_on_other_requests_should_not_be_treated_as_release_rejection()
        {
            WithResponse("{\"status\":false}");

            Assert.Throws<DownloadClientException>(() => Subject.GetQueue(0, 0, _settings));
        }

        [Test]
        public void accepted_add_should_preserve_exact_download_id()
        {
            WithResponse("{\"status\":true,\"nzo_ids\":[\"SABnzbd_nzo_test\"]}");

            var response = Subject.DownloadNzb(new byte[] { 1 }, "movie.nzb", "movies", 0, _settings);

            Assert.That(response.Ids, Is.EqualTo(new[] { "SABnzbd_nzo_test" }));
        }

        [Test]
        public void lost_add_response_should_remain_ambiguous()
        {
            Mocker.GetMock<IHttpClient>().Setup(c => c.Execute(It.IsAny<HttpRequest>())).Throws(new TimeoutException("Response lost"));

            Assert.Throws<TimeoutException>(() => Subject.DownloadNzb(new byte[] { 1 }, "movie.nzb", "movies", 0, _settings));
        }
    }
}
