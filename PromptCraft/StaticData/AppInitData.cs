
using BaseClassLib.Models;
using PromptCraft.Common;
using PromptCraft.Data;
using PromptCraft.Service;
using Ke.Bee.Localization.Localizer;
using System;
using System.Collections.Generic;

namespace PromptCraft.StaticData
{
    public class AppInitData
    {
        public static Config? Config { get; set; }

        public static List<ListItem<LanguageEnum>> Langues { get; set; } = new()
        {
            new(LanguageEnum.zh_CN,"简体中文",0),
            new(LanguageEnum.en_US,"en-US",1)
        };

        /// <summary>
        /// 初始化配置：DB 优先，JSON 兜底
        /// </summary>
        public static void InitAppConfigData()
        {
            // 1. 尝试从 SQLite 加载
            try
            {
                var dbConfig = ConfigRepository.LoadFromDb();
                if (dbConfig != null)
                {
                    Config = ConfigRepository.ToJsonConfig(dbConfig);
                    LogService.Instance.Info(Localizer.Instance?["ConfigLoadedFromSqlite"] ?? "", "Config");
                    return;
                }
            }
            catch (Exception ex)
            {
                // DB 不可用时 fallback 到 JSON
                LogService.Instance.Warn(Localizer.Instance?["ConfigSqliteFallback"] ?? "", "Config", ex);
            }

            // 2. 从 JSON 加载（兜底）
            string path = StorageService.AppCurrentData;
            Config = StorageService.LoadConfig<Config>(path);
            LogService.Instance.Info(string.Format(Localizer.Instance?["ConfigLoadedFromJson"] ?? "", path), "Config");
        }

        /// <summary>
        /// 保存配置到 DB（同时更新内存）
        /// </summary>
        public static void SaveConfig()
        {
            if (Config == null) return;
            var appConfig = ConfigRepository.FromJsonConfig(Config);
            ConfigRepository.SaveToDb(appConfig);
            LogService.Instance.Debug(Localizer.Instance?["ConfigSavedToSqlite"] ?? "", "Config");
        }
    }
}
