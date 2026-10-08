using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Ke.Bee.Localization.Localizer;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PromptCraft.Common
{
    internal static class StorageService
    {
        public static FilePickerFileType All => new(Localizer.Instance?["AllFiles"] ?? "")
        {
            Patterns = ["*.*"],
            MimeTypes = ["*/*"]
        };

        public static FilePickerFileType Json { get; } = new("Json")
        {
            Patterns = ["*.json"],
            AppleUniformTypeIdentifiers = ["public.json"],
            MimeTypes = ["application/json"]
        };

        public static IStorageProvider? GetStorageProvider()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                return window.StorageProvider;
            }

            if (Avalonia.Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime
                {
                    MainView: { } mainView
                })
            {
                var visualRoot = TopLevel.GetTopLevel(mainView);
                if (visualRoot is TopLevel topLevel)
                {
                    return topLevel.StorageProvider;
                }
            }

            return null;
        }


        // 获取应用程序数据目录
        public static string GetAppDataDirectory(string appName)
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appDir = Path.Combine(baseDir, appName);
            Directory.CreateDirectory(appDir);
            return appDir;
        }

        // 获取用户文档目录
        public static string GetDocumentsDirectory()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        // 获取桌面目录
        public static string GetDesktopDirectory()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        // 安全的文件名处理
        public static string SanitizeFileName(string fileName)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            return string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        }

        public static T? LoadConfig<T>(string filePath, string fileName = "config.json") where T : new()
        {
            if (!Directory.Exists(filePath))
            {
                Directory.CreateDirectory(filePath);
            }
            filePath = Path.Combine(filePath, fileName);
            if (!File.Exists(filePath))
            {
                return default; // 返回默认值
            }
            string content = File.ReadAllText(filePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<T>(content) ?? new T();
        }
        public static async Task SaveConfigAsync<T>(T config, string filePath, string fileName = "config.json")
        {
            if (!Directory.Exists(filePath))
            {
                Directory.CreateDirectory(filePath);
            }
            filePath = Path.Combine(filePath, fileName);
            string jsonContent = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(filePath, jsonContent, Encoding.UTF8);
        }

        public static void SaveConfig<T>(T config, string filePath, string fileName = "config.json")
        {
            if (!Directory.Exists(filePath))
            {
                Directory.CreateDirectory(filePath);
            }
            filePath = Path.Combine(filePath, fileName);
            string jsonContent = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(filePath, jsonContent, Encoding.UTF8);
        }

        public static async Task SaveTextFileAsync(string content, string filePath, string fileName)
        {
            if (!Directory.Exists(filePath))
            {
                Directory.CreateDirectory(filePath);
            }
            filePath = Path.Combine(filePath, fileName);
            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);
        }

        public static string AppCurrentData => GetAppDataPath();

        private static string GetAppDataPath(string appName = "PromptCraft")
        {
            var _appDataPath = Path.Combine(
                  Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                  appName
              );

            // 确保目录存在
            Directory.CreateDirectory(_appDataPath);
            return _appDataPath;
        }
    }
}