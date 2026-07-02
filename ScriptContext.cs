using Idm.Functions;

namespace ScriptContext;

/// <summary>
/// Контекст выполнения скриптов. Содержит объекты, которые уже созданы средой.
/// </summary>
public static class ScriptContext
{
    /// <summary>
    /// Основной клиент для работы с IDM (уже создан средой)
    /// </summary>
    public static IdmClientFunctions IdmWebClient { get; set; }

    // === Добавляй сюда другие объекты по мере необходимости ===
    // public static ДругойКласс ДругаяПеременная { get; set; }
    // public static ЕщеОдинКласс ЕщеОднаПеременная { get; set; }
}