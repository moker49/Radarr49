using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Localization
{
    [TestFixture]
    public class LocalizationServiceFixture : CoreTest<LocalizationService>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IConfigService>().Setup(m => m.UILanguage).Returns((int)Language.English);

            Mocker.GetMock<IAppFolderInfo>().Setup(m => m.StartUpFolder).Returns(TestContext.CurrentContext.TestDirectory);
        }

        [Test]
        public void all_shipped_translations_must_be_valid_utf8_string_dictionaries()
        {
            var files = Directory.GetFiles(Path.Combine(TestContext.CurrentContext.TestDirectory, "Localization", "Core"), "*.json");
            Assert.That(files, Is.Not.Empty);
            foreach (var file in files)
            {
                Assert.DoesNotThrowAsync(
                    async () =>
                    {
                        await using var stream = File.OpenRead(file);
                        await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream);
                    },
                    file);
            }
        }

        [TestCase("Verifying", "Verifying")]
        [TestCase("VerificationFailed", "Verification failed")]
        [TestCase("PublishedVerifying", "Published, verifying file integrity")]
        public void import_status_translations_must_load_through_the_localization_service(string key, string expected)
        {
            Assert.That(Subject.GetLocalizationDictionary()[key], Is.EqualTo(expected));
        }

        [Test]
        public void should_get_string_in_dictionary_if_lang_exists_and_string_exists()
        {
            var localizedString = Subject.GetLocalizedString("UILanguage");

            localizedString.Should().Be("UI Language");
        }

        [Test]
        public void should_get_string_in_french()
        {
            Mocker.GetMock<IConfigService>().Setup(m => m.UILanguage).Returns((int)Language.French);

            var localizedString = Subject.GetLocalizedString("UILanguage");

            localizedString.Should().Be("Langue de l'IU");

            ExceptionVerification.ExpectedErrors(0);
        }

        [Test]
        public void should_get_string_in_default_dictionary_if_unknown_language_and_string_exists()
        {
            Mocker.GetMock<IConfigService>().Setup(m => m.UILanguage).Returns(0);
            var localizedString = Subject.GetLocalizedString("UILanguage");

            localizedString.Should().Be("UI Language");
        }

        [Test]
        public void should_return_argument_if_string_doesnt_exists()
        {
            var localizedString = Subject.GetLocalizedString("badString");

            localizedString.Should().Be("badString");
        }

        [Test]
        public void should_return_argument_if_string_doesnt_exists_default_lang()
        {
            var localizedString = Subject.GetLocalizedString("badString");

            localizedString.Should().Be("badString");
        }

        [Test]
        public void should_throw_if_empty_string_passed()
        {
            Assert.Throws<ArgumentNullException>(() => Subject.GetLocalizedString(""));
        }

        [Test]
        public void should_throw_if_null_string_passed()
        {
            Assert.Throws<ArgumentNullException>(() => Subject.GetLocalizedString(null));
        }
    }
}
