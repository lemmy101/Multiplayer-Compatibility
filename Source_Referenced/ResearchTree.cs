
using HarmonyLib;
using Multiplayer.API;
using Storefront.Store;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluffyResearchTree;
using Verse;
using RimWorld;

namespace Multiplayer.Compat
{
    [MpCompatFor("Mlie.ResearchTree")]
    public class ResearchTree
    {
        public ResearchTree(ModContentPack mod)
        {
            var queue = AccessTools.TypeByName("FluffyResearchTree.Queue");

            // Sync when a research project is changed in the research tree ui
            MP.RegisterSyncMethod(typeof(Queue), nameof(Queue.Dequeue));
            MP.RegisterSyncMethod(typeof(Queue), nameof(Queue.EnqueueRange));
            MP.RegisterSyncMethod(queue, nameof(Queue.RefreshQueue));

            // Sync when tree is initialized
            MP.RegisterSyncMethod(typeof(Tree), nameof(Tree.Initialize));

            // Sync worker for research node to be serialized to research name and back
            MP.RegisterSyncWorker<FluffyResearchTree.ResearchNode>(SyncResearchNode, typeof(FluffyResearchTree.ResearchNode));
            MP.RegisterSyncWorker<IEnumerable<FluffyResearchTree.ResearchNode>>(SyncResearchNodes, typeof(FluffyResearchTree.ResearchNode));
        }
         
        public static void SyncResearchNode(SyncWorker worker, ref ResearchNode node)
        {
            if (worker.isWriting)
            {
                worker.Write(node.Research.defName);
            }
            else
            {
                string researchDef = worker.Read<string>();
                node = (DefDatabase<ResearchProjectDef>.GetNamed(researchDef)).ResearchNode();
            }
        }

        public static void SyncResearchNodes(SyncWorker worker, ref IEnumerable<ResearchNode> nodes)
        {
            if (worker.isWriting)
            {
                worker.Write(nodes.Select(node => node.Research.defName));
            }
            else
            {
                var defNames = worker.Read<IEnumerable<string>>();
                nodes = defNames.Select(name => (DefDatabase<ResearchProjectDef>.GetNamed(name)).ResearchNode());
            }
        }


    }
}
