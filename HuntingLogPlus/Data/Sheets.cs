using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace HuntingLogPlus.Data;

public static class Sheets
{
    public static readonly ExcelSheet<Map> Map = Plugin.DataManager.GetExcelSheet<Map>();
    public static readonly ExcelSheet<BNpcName> BNpcName = Plugin.DataManager.GetExcelSheet<BNpcName>();
    public static readonly ExcelSheet<ClassJob> ClassJob = Plugin.DataManager.GetExcelSheet<ClassJob>();
    public static readonly ExcelSheet<Aetheryte> Aetheryte = Plugin.DataManager.GetExcelSheet<Aetheryte>();
    public static readonly ExcelSheet<GrandCompany> GrandCompany = Plugin.DataManager.GetExcelSheet<GrandCompany>();
    public static readonly SubrowExcelSheet<MapMarker> MapMarker = Plugin.DataManager.GetSubrowExcelSheet<MapMarker>();
    public static readonly ExcelSheet<TerritoryType> TerritoryType = Plugin.DataManager.GetExcelSheet<TerritoryType>();
    public static readonly ExcelSheet<ContentFinderCondition> ContentFinderCondition = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
}
