using System;
using System.IO;
using System.Text.Json;

namespace SymbolsTool.Services;

public static class SettingsService
{
	private static string Dir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

	private static string FilePath => Path.Combine(Dir, "settings.json");

	public static AppSettings Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				AppSettings appSettings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
				if (appSettings != null)
				{
					return appSettings;
				}
			}
		}
		catch
		{
		}
		return new AppSettings();
	}

	public static void Save(AppSettings settings)
	{
		try
		{
			Directory.CreateDirectory(Dir);
			File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
			{
				WriteIndented = true
			}));
		}
		catch
		{
		}
	}
}
