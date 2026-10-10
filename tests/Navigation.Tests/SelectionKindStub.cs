// Test-harness stand-in, compiled ONLY into this throwaway test project.
//
// Why this file exists: `Destination.cs` (linked below) names
// `MapSelectionMemory.SelectionKind`, and the real `MapSelectionMemory` pulls in
// REFrameworkNET — which these dependency-free tests deliberately never reference.
// Everything the destinations and the picker use of that type is the enum itself,
// so the four members mirror the real declaration exactly. Keep them in step:
//     SF6Access/Services/WorldTour/MapSelectionMemory.cs
//     public enum SelectionKind { Point, LadderUp, LadderDown, Door }
namespace SF6Access.Services.WorldTour;

public static class MapSelectionMemory
{
    public enum SelectionKind { Point, LadderUp, LadderDown, Door }
}
