using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SymbolsTool.Services;

internal static class SymbolSearchService
{
	private const int ScoreCharExact = 1000;

	private const int ScoreNameExact = 900;

	private const int ScoreCharContains = 800;

	private const int ScoreNamePrefix = 700;

	private const int ScoreNameContains = 500;

	private const int ScoreCategory = 200;

	public static List<SymbolItem> Search(string keyword)
	{
		string text = Normalize(keyword);
		if (text.Length == 0)
		{
			return new List<SymbolItem>();
		}
		List<(SymbolItem, int)> list = new List<(SymbolItem, int)>();
		foreach (SymbolItem symbol in SymbolDatabaseService.Symbols)
		{
			int num = Score(symbol, text);
			if (num > 0)
			{
				list.Add((symbol, num));
			}
		}
		return list.OrderByDescending(((SymbolItem Item, int Score) h) => h.Score).ThenBy(((SymbolItem Item, int Score) h) => h.Item.Order).Select(((SymbolItem Item, int Score) h) => h.Item)
			.DistinctBy((SymbolItem i) => i.Char)
			.ToList();
	}

	private static int Score(SymbolItem item, string q)
	{
		if (item.NormChar == q)
		{
			return 1000;
		}
		string[] normZhNames = item.NormZhNames;
		for (int i = 0; i < normZhNames.Length; i++)
		{
			if (normZhNames[i] == q)
			{
				return 900;
			}
		}
		normZhNames = item.NormEnTokens;
		for (int i = 0; i < normZhNames.Length; i++)
		{
			if (normZhNames[i] == q)
			{
				return 900;
			}
		}
		if (item.NormChar.Contains(q, StringComparison.Ordinal))
		{
			return 800 - Math.Min(item.NormChar.Length - q.Length, 50);
		}
		normZhNames = item.NormZhNames;
		for (int i = 0; i < normZhNames.Length; i++)
		{
			if (normZhNames[i].StartsWith(q, StringComparison.Ordinal))
			{
				return 700;
			}
		}
		if (q.Length >= 3)
		{
			normZhNames = item.NormEnTokens;
			for (int i = 0; i < normZhNames.Length; i++)
			{
				if (normZhNames[i].StartsWith(q, StringComparison.Ordinal))
				{
					return 650;
				}
			}
		}
		normZhNames = item.NormZhNames;
		for (int i = 0; i < normZhNames.Length; i++)
		{
			if (normZhNames[i].Contains(q, StringComparison.Ordinal))
			{
				return 500;
			}
		}
		if (q.Length >= 3 && item.NormEnName.Contains(q, StringComparison.Ordinal))
		{
			return 450;
		}
		if (item.NormCategory.Contains(q, StringComparison.Ordinal))
		{
			return 200;
		}
		return 0;
	}

	public static string Normalize(string text)
	{
		return text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace("\ufe0f", "")
			.Replace("\u20e3", "")
			.Trim();
	}
}
