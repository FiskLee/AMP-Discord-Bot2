using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using System.Resources;

namespace DiscordBotPlugin
{
    public class ResourceReader
    {
        private readonly ResourceManager _resourceManager;
        private readonly CultureInfo? _cultureInfo;

        // Constructor to initialize ResourceManager and CultureInfo
        public ResourceReader(string baseName, Assembly assembly, CultureInfo? cultureInfo = null)
        {
            _resourceManager = new ResourceManager(baseName, assembly);
            _cultureInfo = cultureInfo ?? CultureInfo.CurrentUICulture;
        }

        public string? ReadResource(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();

            // Resource name should include namespace
            string resourcePath = $"{assembly.GetName().Name}.{resourceName}";

            try
            {
                using (Stream? stream = assembly.GetManifestResourceStream(resourcePath))
                {
                    if (stream == null)
                    {
                        Console.WriteLine($"Warning: Resource '{resourceName}' not found in assembly '{assembly.FullName}'.");
                        return null;
                    }
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading resource '{resourceName}': {ex.Message}");
                return null;
            }
        }

        public string? GetString(string key)
        {
            try
            {
                // Use CurrentUICulture if _cultureInfo is null
                CultureInfo culture = _cultureInfo ?? CultureInfo.CurrentUICulture;
                #pragma warning disable CS8600 // Suppress warning for GetString potentially returning null
                string? resourceValue = _resourceManager.GetString(key, culture);
                #pragma warning restore CS8600
                return resourceValue ?? string.Empty;
            }
            catch (MissingManifestResourceException ex) {
                Console.Error.WriteLine($"Resource error for key '{key}': {ex.Message}. Ensure resource file exists and key is correct.");
                return $"MISSING_RESOURCE({key})"; // Return indicator on error
            }
            catch (Exception ex)
            {
                // Use Console.Error.WriteLine for logging errors from utility classes
                Console.Error.WriteLine($"Error retrieving resource string '{key}': {ex.Message}");
                return string.Empty; // Return empty string on general error
            }
        }
    }
}
