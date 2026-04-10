using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EvacSim.Environment
{
public class AttributeValue
{
    public string Key { get; set; }
    public string Type { get; set; }

    private object value;
    
    /// <summary>
    /// ゲッター (get) では、Type が "AttributeSet" である場合には AttributeSetValue を返し、それ以外の場合には value を返します。これは、Type に応じて異なる値を返すための条件付きロジックを実装しています。
    /// セッター (set) では、Type が "AttributeSet" であり、かつ value が JArray 型である場合に特別な処理を行います。この場合、value を JArray としてキャストし、それを List<AttributeValue> に変換して AttributeSetValue に設定します。JArray は JSON 配列を表す型であり、これをリストに変換することで、JSON データをオブジェクトのリストとして扱えるようにしています。
    /// </summary>
    [JsonProperty("value")]
    public object Value
    {
        get => Type == "AttributeSet" ? AttributeSetValue : value;
        set
        {
            if (Type == "AttributeSet" && value is JArray jArray)
            {
                // JSON配列をList<AttributeValue>に変換
                AttributeSetValue = jArray.ToObject<List<AttributeValue>>();
            }
            else
            {
                this.value = value;
            }
        }
    }

    // "type": "AttributeSet"の場合にデシリアライズされるプロパティ
    [JsonIgnore] // このプロパティはJSONに含めない
    public List<AttributeValue> AttributeSetValue { get; private set; }
}

public class RootObject
{
    public string GmlID { get; set; }
    public List<int> CityObjectIndex { get; set; }
    public string CityObjectType { get; set; }
    public List<AttributeValue> Attributes { get; set; }
}
}