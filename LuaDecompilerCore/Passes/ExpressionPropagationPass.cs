﻿using System.Collections.Generic;
using LuaDecompilerCore.Analyzers;
using LuaDecompilerCore.IR;

namespace LuaDecompilerCore.Passes;

/// <summary>
/// Performs expression propagation which substitutes register definitions into their users to build more complex
/// expressions
/// </summary>
public class ExpressionPropagationPass : IPass
{
    public bool RunOnFunction(DecompilationContext decompilationContext, FunctionContext functionContext, Function f)
    {
        var irChanged = false;
        
        // GetUses calls have a lot of allocation overhead so reusing the same set has huge perf gains.
        var usesSet = new HashSet<Identifier>(10);

        var localVariableAnalysis = functionContext.GetAnalysis<LocalVariablesAnalyzer>();
        var defineUseAnalysis = functionContext.GetAnalysis<IdentifierDefinitionUseAnalyzer>();

        bool changed;
        do
        {
            changed = false;
            foreach (var b in f.BlockList)
            {
                for (var i = 1; i < b.Instructions.Count; i++)
                {
                    var inst = b.Instructions[i];
                    usesSet.Clear();
                    inst.GetUses(usesSet, true);
                    foreach (var use in usesSet)
                    {
                        var definingInstruction = defineUseAnalysis.DefiningInstruction(use);
                        bool isListElementDefine = definingInstruction?.GetSingleDefine(true) is { } def &&
                                                   defineUseAnalysis.IsListInitializerElement(def);
                        if (definingInstruction is Assignment
                            {
                                IsSingleAssignment: true, 
                                LocalAssignments: null, 
                                Right: not null
                            } a &&
                            ((defineUseAnalysis.UseCount(use) == 1 && 
                              (((i - 1 >= 0 && b.Instructions[i - 1] == definingInstruction || isListElementDefine) &&
                                definingInstruction.OriginalBlock == inst.OriginalBlock) || 
                               inst is Assignment { IsListAssignment: true }) && 
                              !localVariableAnalysis.LocalVariables.Contains(use)) ||
                             (inst is Assignment
                             {
                                 IsLocalDeclaration: true, 
                                 IsSingleAssignment: true,
                                 Left: { HasIndex: false, Identifier: { IsRegister: true, RegNum: {} reg} }
                             } && use.RegNum == reg) ||
                             a.PropagateAlways))
                        {
                            // Don't substitute if this use's define was defined before the code gen for the function
                            // call even began
                            if (!a.PropagateAlways && inst is Assignment { Right: FunctionCall fc } && 
                                definingInstruction.InstructionIndices.End - 1 < fc.FunctionDefIndex)
                            {
                                continue;
                            }
                            if (!a.PropagateAlways && inst is Return { ReturnExpressions: [FunctionCall fc2] } && 
                                definingInstruction.InstructionIndices.End - 1 < fc2.FunctionDefIndex)
                            {
                                continue;
                            }
                            
                            // Don't inline a function call into a non-tail call return because the Lua compiler would
                            // have generated a tail call instead if this weren't a local
                            if (a is { PropagateAlways: false, Right: FunctionCall } &&
                                inst is Return
                                {
                                    IsTailReturn: false, ReturnExpressions: [IdentifierReference { HasIndex: false }]
                                })
                            {
                                continue;
                            }
                            
                            if (inst.ReplaceUses(use, a.Right))
                            {
                                var definingBlock = f.BlockList[defineUseAnalysis.DefiningInstructionBlock(use)];
                                irChanged = true;
                                changed = true;
                                inst.Absorb(a);
                                definingBlock.Instructions.Remove(a);
                                f.SsaVariables.Remove(use);
                                if (b == definingBlock)
                                {
                                    i = -1;
                                }
                            }
                        }
                    }
                }
            }

            // Lua might generate the following (decompiled) code when doing a this call on a global variable:
            //     REG0 = someGlobal
            //     REG0:someFunction(blah...)
            // This rewrites such statements to
            //     someGlobal:someFunction(blah...)
            foreach (var b in f.BlockList)
            {
                for (var i = 0; i < b.Instructions.Count; i++)
                {
                    var inst = b.Instructions[i];
                    if (inst is Assignment { Right: FunctionCall { Args.Count: > 0 } fc } a &&
                        fc.Args[0] is IdentifierReference { HasIndex: false } ir &&
                        defineUseAnalysis.UseCount(ir.Identifier) == 2 &&
                        i > 0 && b.Instructions[i - 1] is Assignment { IsSingleAssignment: true, Left.HasIndex: false } a2 && 
                        a2.Left.Identifier == ir.Identifier &&
                        a2.Right is IdentifierReference or Constant)
                    {
                        a.ReplaceUses(a2.Left.Identifier, a2.Right);
                        a.Absorb(a2);
                        b.Instructions.RemoveAt(i - 1);
                        i--;
                        changed = true;
                        irChanged = true;
                    }
                    
                    // match tail calls
                    if (inst is Return { ReturnExpressions: [FunctionCall { Args.Count: > 0 } fc2] } ret &&
                        fc2.Args[0] is IdentifierReference { HasIndex: false } ir2 &&
                        defineUseAnalysis.UseCount(ir2.Identifier) == 2 &&
                        i > 0 && b.Instructions[i - 1] is Assignment { IsSingleAssignment: true, Left.HasIndex: false } a4 && 
                        a4.Left.Identifier == ir2.Identifier &&
                        a4.Right is IdentifierReference or Constant)
                    {
                        ret.ReplaceUses(a4.Left.Identifier, a4.Right);
                        ret.Absorb(a4);
                        b.Instructions.RemoveAt(i - 1);
                        i--;
                        changed = true;
                        irChanged = true;
                    }
                }
            }
        } while (changed);
        
        functionContext.InvalidateAnalysis<IdentifierDefinitionUseAnalyzer>();
        functionContext.InvalidateAnalysis<LocalVariablesAnalyzer>();

        return irChanged;
    }
}