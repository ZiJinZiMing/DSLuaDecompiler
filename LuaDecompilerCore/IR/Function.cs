﻿using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LuaDecompilerCore.CFG;

namespace LuaDecompilerCore.IR
{
    /// <summary>
    /// A Lua function. A function contains a CFG, a list of instructions, and child functions used for closures
    /// </summary>
    public sealed class Function
    {
        public List<Identifier> Parameters { get; private set; }
        public List<Function> Closures { get; }

        public Dictionary<uint, Label> Labels { get; }

        /// <summary>
        /// When the CFG has been converted to an AST
        /// </summary>
        public bool IsAst { get; set; }

        /// <summary>
        /// The first basic block in which control flow enters upon the function being called
        /// </summary>
        public BasicBlock BeginBlock { get; set; }

        /// <summary>
        /// The final (empty) basic block that is the successor to the end of the function and any blocks that end with a return instruction
        /// </summary>
        public BasicBlock EndBlock { get; set; }

        /// <summary>
        /// List of all the blocks for some analyses
        /// </summary>
        public List<BasicBlock> BlockList { get; }

        /// <summary>
        /// Identifiers that are used in more than one basic block
        /// </summary>
        public HashSet<Identifier> GlobalIdentifiers { get; }

        /// <summary>
        /// All the renamed SSA variables
        /// </summary>
        public HashSet<Identifier> SsaVariables { get; private set; }
        
        /// <summary>
        /// Unique identifier for the function used for various purposes
        /// </summary>
        public int FunctionId { get; }

        public bool InsertDebugComments { get; set; }

        public List<LuaFile.Local>? ArgumentNames = null;

        public bool IsVarargs = false;

        /// <summary>
        /// Number of upvalues this function uses
        /// </summary>
        public int UpValueCount = 0;

        /// <summary>
        /// For each upvalue in Lua 5.3, the register in the parent its bound to
        /// </summary>
        public readonly List<int> UpValueRegisterBinding = new();

        /// <summary>
        /// For each upvalue in Lua 5.3, if the upvalue exists on the stack
        /// </summary>
        public readonly List<bool> UpValueIsStackBinding = new();

        /// <summary>
        /// Upvalue binding symbol from parent closure
        /// </summary>
        public readonly List<Identifier> UpValueBindings = new();

        /// <summary>
        /// List of UpValue gets that will need to be mutated once intra-function UpValue closure binding is done
        /// </summary>
        public readonly List<Assignment> GetUpValueInstructions = new();
        
        /// <summary>
        /// List of UpValue sets that will need to be mutated once intra-function UpValue closure binding is done
        /// </summary>
        public readonly List<Assignment> SetUpValueInstructions = new();

        /// <summary>
        /// Identifiers that have been statically determined to be local variables and should not be inlined via
        /// expression propagation
        /// </summary>
        public HashSet<Identifier> LocalVariables { get; private set; } = new();
        
        private int _currentBlockId;

        private readonly Dictionary<string, Identifier> _symbols = new();

        public Function(int functionId)
        {
            Parameters = new List<Identifier>();
            Closures = new List<Function>();
            Labels = new Dictionary<uint, Label>();
            BlockList = new List<BasicBlock>();
            GlobalIdentifiers = new HashSet<Identifier>();
            SsaVariables = new HashSet<Identifier>();
            FunctionId = functionId;
            
            // Create initial basic block
            BlockList.Add(CreateBasicBlock());
            BeginBlock = EndBlock = BlockList[0];
        }

        public void AddClosure(Function fun)
        {
            Closures.Add(fun);
        }

        public Function LookupClosure(uint i)
        {
            return Closures[(int)i];
        }

        public void SetParameters(List<Identifier> parameters)
        {
            Parameters = parameters;
        }

        public Label GetLabel(uint pc)
        {
            if (Labels.TryGetValue(pc, out var value))
            {
                value.UsageCount++;
                return Labels[pc];
            }
            var label = new Label
            {
                OpLocation = (int)pc,
                UsageCount = 1
            };
            Labels.Add(pc, label);
            return label;
        }
        
        /// <summary>
        /// Gets or looks up a new register identifier and inserts it into the local symbol table
        /// </summary>
        /// <param name="reg">Register number</param>
        /// <returns>Identifier representing this register</returns>
        public Identifier GetRegister(uint reg)
        {
            if (_symbols.TryGetValue($@"REG{reg}", out var value)) return value;
            var regi = new Identifier
            {
                Type = Identifier.IdentifierType.Register,
                Name = $@"REG{reg}",
                RegNum = reg
            };
            _symbols.Add(regi.Name, regi);
            return _symbols[$@"REG{reg}"];
        }
        
        /// <summary>
        /// Gets all the register identifiers in this function
        /// </summary>
        /// <returns>Set of registers in this function</returns>
        public HashSet<Identifier> GetAllRegisters()
        {
            var ret = new HashSet<Identifier>();
            foreach (var reg in _symbols)
            {
                if (reg.Value.Type == Identifier.IdentifierType.Register)
                {
                    ret.Add(reg.Value);
                }
            }
            return ret;
        }
        
        /// <summary>
        /// Gets or looks up a new UpValue identifier and inserts it into the local symbol table
        /// </summary>
        /// <param name="upValue">UpValue number</param>
        /// <returns>Identifier representing this UpValue</returns>
        public Identifier GetUpValue(uint upValue)
        {
            if (_symbols.TryGetValue($@"UPVAL{upValue}", out var value)) return value;
            var regi = new Identifier
            {
                Type = Identifier.IdentifierType.UpValue,
                RegNum = upValue,
                Name = $@"UPVAL{upValue}"
            };
            _symbols.Add(regi.Name, regi);
            return _symbols[$@"UPVAL{upValue}"];
        }

        public BasicBlock CreateBasicBlock()
        {
            return new BasicBlock(_currentBlockId++);
        }
        
        public BasicBlock CreateAndAddBasicBlock()
        {
            var b = new BasicBlock(_currentBlockId++);
            BlockList.Add(b);
            return b;
        }

        /// <summary>
        /// Computes the dominance sets for all the nodes as well as the dominance tree
        /// </summary>
        public void ComputeDominance()
        {
            // Use Cooper-Harvey-Kennedy algorithm for fast computation of dominance
            // http://www.hipersoft.rice.edu/grads/publications/dom14.pdf

            // List the blocks in reverse postorder and initialize the immediate dominator
            var reversePostorderBlocks = NumberReversePostorder(false);
            foreach (var block in reversePostorderBlocks)
            {
                block.ImmediateDominator = block;
                block.Dominance.Clear();
                block.DominanceTreeSuccessors.Clear();
            }

            BasicBlock Intersect(BasicBlock b1, BasicBlock b2)
            {
                var finger1 = b1;
                var finger2 = b2;
                while (finger1 != finger2)
                {
                    while (finger1.ReversePostorderNumber > finger2.ReversePostorderNumber)
                    {
                        finger1 = reversePostorderBlocks[finger1.ReversePostorderNumber].ImmediateDominator;
                    }

                    while (finger2.ReversePostorderNumber > finger1.ReversePostorderNumber)
                    {
                        finger2 = reversePostorderBlocks[finger2.ReversePostorderNumber].ImmediateDominator;
                    }
                }

                return finger1;
            }
            
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var block in reversePostorderBlocks)
                {
                    // Begin block is always its only dominator
                    if (block == BeginBlock) continue;
                    
                    // Intersect all the predecessors to find the new dominator
                    var firstProcessed = block.Predecessors
                        .First(b => block.ReversePostorderNumber > b.ReversePostorderNumber);
                    var newDominator = firstProcessed;
                    foreach (var predecessor in block.Predecessors)
                    {
                        if (predecessor == firstProcessed) continue;
                        if (predecessor.ImmediateDominator != predecessor || predecessor == BeginBlock)
                        {
                            newDominator = Intersect(predecessor, newDominator);
                        }
                    }

                    // if dominator is unchanged go to the next block
                    if (block.ImmediateDominator == newDominator) continue;
                    
                    // We have a new dominator
                    block.ImmediateDominator = newDominator;
                    changed = true;
                }
            }
            
            // Now build the dominance set and dominance tree
            foreach (var block in reversePostorderBlocks)
            {
                block.Dominance.Add(block);
                if (block.ImmediateDominator == block) continue;
                block.Dominance.Add(block.ImmediateDominator);
                block.ImmediateDominator.DominanceTreeSuccessors.Add(block);
                block.Dominance.UnionWith(block.ImmediateDominator.Dominance);
            }
        }

        /// <summary>
        /// Compute the dominance frontier for each blockFollow = {BasicBlock} null 
        /// </summary>
        public void ComputeDominanceFrontier()
        {
            ComputeDominance();
            foreach (var t in BlockList)
            {
                if (t.Predecessors.Count > 1)
                {
                    foreach (var p in t.Predecessors)
                    {
                        var runner = p;
                        while (runner != t.ImmediateDominator)
                        {
                            runner.DominanceFrontier.UnionWith(new[] { t });
                            runner = runner.ImmediateDominator;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Compute global liveness information for all registers in the function
        /// </summary>
        public void ComputeGlobalLiveness(HashSet<Identifier> allRegisters)
        {
            ComputeDominanceFrontier();
            foreach (var b in BlockList)
            {
                b.KilledIdentifiers.Clear();
                b.UpwardExposedIdentifiers.Clear();
                b.LiveOut.Clear();
                GlobalIdentifiers.UnionWith(b.ComputeKilledAndUpwardExposed());
            }

            // Compute live out for each block iteratively
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var b in BlockList)
                {
                    var temp = new HashSet<Identifier>();
                    foreach (var successor in b.Successors)
                    {
                        var equation = new HashSet<Identifier>(allRegisters);
                        foreach (var kill in successor.KilledIdentifiers)
                        {
                            equation.Remove(kill);
                        }
                        equation.IntersectWith(successor.LiveOut);
                        equation.UnionWith(successor.UpwardExposedIdentifiers);
                        temp.UnionWith(equation);
                    }
                    if (!b.LiveOut.SetEquals(temp))
                    {
                        b.LiveOut = temp;
                        changed = true;
                    }
                }
            }
        }

        public List<BasicBlock> PostorderTraversal(bool reverse, bool skipEndBlock = true)
        {
            var ret = new List<BasicBlock>();
            var visited = new HashSet<BasicBlock>();
            if (skipEndBlock)
            {
                visited.Add(EndBlock);
            }

            void Visit(BasicBlock b)
            {
                visited.Add(b);
                foreach (var successor in b.Successors)
                {
                    if (!visited.Contains(successor))
                    {
                        Visit(successor);
                    }
                }
                ret.Add(b);
            }

            Visit(BeginBlock);

            if (reverse)
            {
                ret.Reverse();
            }
            return ret;
        }

        /// <summary>
        /// Labels all the blocks in the CFG with a number in order of their reverse postorder traversal
        /// </summary>
        public List<BasicBlock> NumberReversePostorder(bool skipEndBlock = true)
        {
            var ordering = PostorderTraversal(true, skipEndBlock);
            for (var i = 0; i < ordering.Count; i++)
            {
                ordering[i].ReversePostorderNumber = i;
            }

            return ordering;
        }
        
        public override string? ToString()
        {
            return FunctionPrinter.DebugPrintFunction(this);
        }
    }
}
