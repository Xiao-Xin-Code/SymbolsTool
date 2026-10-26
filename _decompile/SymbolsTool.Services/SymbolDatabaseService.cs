using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SymbolsTool.Services;

public static class SymbolDatabaseService
{
	public static List<string> CategoryNames { get; } = SymbolData.All.Select(((string Category, string[] Symbols) x) => x.Category).ToList();

	public static List<SymbolItem> Symbols { get; } = Build();

	public static List<string> GetCategories()
	{
		return CategoryNames;
	}

	public static List<SymbolItem> GetByCategory(string category)
	{
		// 按分类缓存列表，切换分类时不再对整个符号库做一次 LINQ 过滤与分配
		if (CategoryCache.TryGetValue(category, out List<SymbolItem> value))
		{
			return value;
		}
		List<SymbolItem> list = Symbols.Where((SymbolItem s) => s.Category == category).ToList();
		CategoryCache[category] = list;
		return list;
	}

	private static readonly Dictionary<string, List<SymbolItem>> CategoryCache = new Dictionary<string, List<SymbolItem>>(StringComparer.Ordinal);

	public static List<SymbolItem> Search(string keyword)
	{
		return SymbolSearchService.Search(keyword);
	}

	private static List<SymbolItem> Build()
	{
		Dictionary<string, string> dictionary = LoadEnglishNames();
		Dictionary<string, (string, string)> dictionary2 = LoadColorGlyphs();
		List<SymbolItem> list = new List<SymbolItem>();
		int num = 0;
		(string, string[])[] all = SymbolData.All;
		for (int i = 0; i < all.Length; i++)
		{
			(string, string[]) tuple = all[i];
			string item = tuple.Item1;
			string[] item2 = tuple.Item2;
			foreach (string text in item2)
			{
				SymbolItem symbolItem = new SymbolItem
				{
					Char = text,
					Category = item
				};
				num = (symbolItem.Order = num + 1);
				symbolItem.ColorText = text;
				symbolItem.InputText = text;
				SymbolItem symbolItem2 = symbolItem;
				if (dictionary.TryGetValue(text, out var value))
				{
					symbolItem2.EnName = value;
				}
				if (dictionary2.TryGetValue(text, out var value2))
				{
					symbolItem2.UseColorRenderer = true;
					(symbolItem2.ColorText, symbolItem2.InputText) = value2;
				}
				if (SymbolAliasData.All.TryGetValue(text, out string value3))
				{
					string[] array = value3.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
					if (array.Length != 0)
					{
						symbolItem2.ZhName = array[0];
					}
					if (array.Length > 1)
					{
						symbolItem2.Aliases = string.Join(";", array.Skip(1));
					}
				}
				symbolItem2.ToolTipText = BuildToolTip(text, symbolItem2.ZhName, symbolItem2.EnName);
				symbolItem2.NormChar = SymbolSearchService.Normalize(text);
				symbolItem2.NormCategory = SymbolSearchService.Normalize(item);
				symbolItem2.NormEnName = SymbolSearchService.Normalize(symbolItem2.EnName);
				symbolItem2.NormEnTokens = (from t in symbolItem2.NormEnName.Split(new char[7] { ' ', '-', '/', ',', '(', ')', ':' }, StringSplitOptions.RemoveEmptyEntries)
					where t.Length > 0
					select t).Distinct().ToArray();
				List<string> list2 = new List<string>();
				if (symbolItem2.ZhName.Length > 0)
				{
					list2.Add(SymbolSearchService.Normalize(symbolItem2.ZhName));
				}
				if (symbolItem2.Aliases.Length > 0)
				{
					list2.AddRange(symbolItem2.Aliases.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(SymbolSearchService.Normalize));
				}
				symbolItem2.NormZhNames = list2.Where((string n) => n.Length > 0).Distinct().ToArray();
				list.Add(symbolItem2);
			}
		}
		return list;
	}

	private static string BuildToolTip(string ch, string zhName, string enName)
	{
		List<string> list = new List<string>();
		if (zhName.Length > 0)
		{
			list.Add(zhName);
		}
		if (enName.Length > 0)
		{
			list.Add(enName);
		}
		if (list.Count == 0)
		{
			list.Add($"U+{char.ConvertToUtf32(ch, 0):X4}");
		}
		return string.Join(" · ", list);
	}

	private static Dictionary<string, string> LoadEnglishNames()
	{
		return LoadTsv("SymbolsTool.Services.SymbolEnglishNames.tsv");
	}

	private static Dictionary<string, (string Display, string Input)> LoadColorGlyphs()
	{
		Dictionary<string, (string, string)> dictionary = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
		using Stream stream = typeof(SymbolDatabaseService).Assembly.GetManifestResourceStream("SymbolsTool.Services.ColorGlyphs.tsv");
		if (stream == null)
		{
			return dictionary;
		}
		using StreamReader streamReader = new StreamReader(stream, Encoding.UTF8);
		string text;
		while ((text = streamReader.ReadLine()) != null)
		{
			string[] array = text.Split('\t');
			if (array.Length >= 2 && array[0].Length != 0)
			{
				string item = array[1];
				string item2 = ((array.Length >= 3 && array[2].Length > 0) ? array[2] : array[0]);
				dictionary[array[0]] = (item, item2);
			}
		}
		return dictionary;
	}

	private static Dictionary<string, string> LoadTsv(string resourceName)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
		using Stream stream = typeof(SymbolDatabaseService).Assembly.GetManifestResourceStream(resourceName);
		if (stream == null)
		{
			return dictionary;
		}
		using StreamReader streamReader = new StreamReader(stream, Encoding.UTF8);
		string text;
		while ((text = streamReader.ReadLine()) != null)
		{
			int num = text.IndexOf('\t');
			if (num > 0 && num < text.Length - 1)
			{
				string key = text.Substring(0, num);
				string value = text.Substring(num + 1);
				dictionary[key] = value;
			}
		}
		return dictionary;
	}
}
