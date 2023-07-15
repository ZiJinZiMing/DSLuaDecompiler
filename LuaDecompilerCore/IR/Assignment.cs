﻿using System.Collections.Generic;
using System.Linq;

namespace LuaDecompilerCore.IR
{
    /// <summary>
    /// IL for an assignment operation
    /// Identifier := Expr
    /// </summary>
    public class Assignment : Instruction
    {
        /// <summary>
        /// Functions can have multiple returns
        /// </summary>
        public List<IdentifierReference> Left;
        public Expression Right;

        /// <summary>
        /// If debug info exist, these are the local variables that are assigned if any (null if none are assigned and thus a "temp")
        /// </summary>
        public List<LuaFile.Local> LocalAssignments;
        
        /// <summary>
        /// When this is set to true, the value defined by this is always expression/constant propogated, even if it's used more than once
        /// </summary>
        public bool PropogateAlways = false;

        /// <summary>
        /// This assignment represents an assignment to an indeterminant number of varargs
        /// </summary>
        public bool IsIndeterminantVararg = false;
        public uint VarargAssignmentReg = 0;

        public uint NilAssignmentReg = 0;

        /// <summary>
        /// Is the first assignment of a local variable, and thus starts with "local"
        /// </summary>
        public bool IsLocalDeclaration = false;

        /// <summary>
        /// If true, the assignment uses "in" instead of "="
        /// </summary>
        public bool IsGenericForAssignment = false;

        /// <summary>
        /// If true, this is a list assignment which affects how expression propogation is done
        /// </summary>
        public bool IsListAssignment = false;

        /// <summary>
        /// If true, this assignment was generated by a self op, which gives some information on how expression propogation is done and the original syntax
        /// </summary>
        public bool IsSelfAssignment = false;

        public Assignment(Identifier l, Expression r)
        {
            Left = new List<IdentifierReference>();
            Left.Add(new IdentifierReference(l));
            Right = r;
        }

        public Assignment(IdentifierReference l, Expression r)
        {
            Left = new List<IdentifierReference>();
            Left.Add(l);
            Right = r;
        }

        public Assignment(List<IdentifierReference> l, Expression r)
        {
            Left = l;
            Right = r;
        }

        public override void Parenthesize()
        {
            Left.ForEach(x => x.Parenthesize());
            if (Right != null)
            {
                Right.Parenthesize();
            }
        }

        public override HashSet<Identifier> GetDefines(bool registersOnly)
        {
            var defines = new HashSet<Identifier>();
            foreach (var id in Left)
            {
                // If the reference is not an indirect one (i.e. not an array access), then it is a definition
                if (!id.HasIndex && (!registersOnly || id.Identifier.Type == Identifier.IdentifierType.Register))
                {
                    defines.Add(id.Identifier);
                }
            }
            return defines;
        }

        public override HashSet<Identifier> GetUses(bool registersOnly)
        {
            var uses = new HashSet<Identifier>();
            foreach (var id in Left)
            {
                // If the reference is an indirect one (i.e. an array access), then it is a use
                if (id.HasIndex && (!registersOnly || id.Identifier.Type == Identifier.IdentifierType.Register))
                {
                    uses.UnionWith(id.GetUses(registersOnly));
                }
                // Indices are also uses
                if (id.HasIndex)
                {
                    foreach (var idx in id.TableIndices)
                    {
                        uses.UnionWith(idx.GetUses(registersOnly));
                    }
                }
            }
            uses.UnionWith(Right.GetUses(registersOnly));
            return uses;
        }

        public override void RenameDefines(Identifier orig, Identifier newIdentifier)
        {
            foreach (var id in Left)
            {
                // If the reference is not an indirect one (i.e. not an array access), then it is a definition
                if (!id.HasIndex && id.Identifier == orig)
                {
                    id.Identifier = newIdentifier;
                    id.Identifier.DefiningInstruction = this;
                }
            }
        }

        public override void RenameUses(Identifier orig, Identifier newIdentifier)
        {
            foreach (var id in Left)
            {
                // If the reference is an indirect one (i.e. an array access), then it is a use
                if (id.HasIndex)
                {
                    id.RenameUses(orig, newIdentifier);
                }
            }
            Right.RenameUses(orig, newIdentifier);
        }

        public override bool ReplaceUses(Identifier orig, Expression sub)
        {
            bool replaced = false;
            foreach (var l in Left)
            {
                replaced = replaced || l.ReplaceUses(orig, sub);
            }
            if (Expression.ShouldReplace(orig, Right))
            {
                replaced = true;
                Right = sub;
            }
            else
            {
                replaced = replaced || Right.ReplaceUses(orig, sub);
            }
            return replaced;
        }

        public override void Accept(IIrVisitor visitor)
        {
            visitor.VisitAssignment(this);
        }

        public override List<Expression> GetExpressions()
        {
            var ret = new List<Expression>();
            foreach (var left in Left)
            {
                ret.AddRange(left.GetExpressions());
            }
            ret.AddRange(Right.GetExpressions());
            return ret;
        }
    }
}
