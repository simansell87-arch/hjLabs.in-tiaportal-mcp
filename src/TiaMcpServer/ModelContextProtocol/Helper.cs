using Siemens.Engineering;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    public class Helper
    {
        public static List<Attribute> GetAttributeList(IEngineeringObject obj)
        {
            var attributes = new List<Attribute>();

            if (obj != null)
            {
                foreach (var attr in obj.GetAttributeInfos())
                {
                    object value;
                    try
                    {
                        value = obj.GetAttribute(attr.Name);
                    }
                    catch (Exception ex)
                    {
                        // Some attributes throw on read (not supported in this context); record the reason instead of failing the whole call.
                        value = $"<unavailable: {ex.Message}>";
                    }

                    attributes.Add(new Attribute
                    {
                        Name = attr.Name,
                        Value = ToJsonSafeValue(value),
                        AccessMode = Enum.GetName(typeof(EngineeringAttributeAccessMode), attr.AccessMode)
                    });
                }
            }

            return attributes;
        }

        /// <summary>
        /// Coerces an attribute value into something System.Text.Json can serialize safely.
        /// Primitives, strings, dates and enums are kept as-is (so JSON keeps its natural type);
        /// any other object (e.g. CultureInfo, which has a self-referential Parent chain) is
        /// converted to its string form to avoid object-cycle / max-depth serialization failures.
        /// </summary>
        public static object? ToJsonSafeValue(object? value)
        {
            if (value == null)
            {
                return null;
            }

            switch (value)
            {
                case string:
                case bool:
                case sbyte:
                case byte:
                case short:
                case ushort:
                case int:
                case uint:
                case long:
                case ulong:
                case float:
                case double:
                case decimal:
                case DateTime:
                case DateTimeOffset:
                case TimeSpan:
                case Guid:
                case Enum:
                    return value;
                default:
                    // Complex/engineering objects: represent as text rather than risking a cyclic graph.
                    try
                    {
                        return value.ToString();
                    }
                    catch (Exception ex)
                    {
                        return $"<unserializable: {ex.Message}>";
                    }
            }
        }

        public static BlockGroupInfo BuildBlockHierarchy(PlcBlockGroup group)
        {
            var groupInfo = new BlockGroupInfo
            {
                Name = group.Name
            };

            var blockList = new List<ResponseBlockInfo>();
            foreach (var block in group.Blocks)
            {
                var attributes = Helper.GetAttributeList(block);
                blockList.Add(new ResponseBlockInfo
                {
                    Name = block.Name,
                    TypeName = block.GetType().Name,
                    Namespace = block.Namespace,
                    ProgrammingLanguage = Enum.GetName(typeof(ProgrammingLanguage), block.ProgrammingLanguage),
                    MemoryLayout = Enum.GetName(typeof(MemoryLayout), block.MemoryLayout),
                    IsConsistent = block.IsConsistent,
                    HeaderName = block.HeaderName,
                    ModifiedDate = block.ModifiedDate,
                    IsKnowHowProtected = block.IsKnowHowProtected,
                    Attributes = attributes,
                    Description = block.ToString()
                });
            }
            groupInfo.Blocks = blockList;

            var groupList = new List<BlockGroupInfo>();
            foreach (var subGroup in group.Groups)
            {
                groupList.Add(BuildBlockHierarchy(subGroup));
            }
            groupInfo.Groups = groupList;

            return groupInfo;
        }
    }
}
