﻿//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.IO;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using Microsoft.Boogie;
using Microsoft.Boogie.VCExprAST;
using Microsoft.Boogie.Clustering;
using Microsoft.Boogie.TypeErasure;
using Microsoft.Boogie.Simplify;

namespace Microsoft.Boogie.TPTP
{
  public class TPTPProcessTheoremProver : LogProverInterface
  {
    private readonly DeclFreeProverContext ctx;

    [ContractInvariantMethod]
    void ObjectInvariant()
    {
      Contract.Invariant(ctx != null);
      Contract.Invariant(AxBuilder != null);
      Contract.Invariant(Namer != null);
      Contract.Invariant(DeclCollector != null);
      Contract.Invariant(BadBenchmarkWords != null);
      Contract.Invariant(cce.NonNullElements(Axioms));
      Contract.Invariant(cce.NonNullElements(TypeDecls));
      Contract.Invariant(_backgroundPredicates != null);

    }


    [NotDelayed]
    public TPTPProcessTheoremProver(ProverOptions options, VCExpressionGenerator gen,
                                      DeclFreeProverContext ctx)
      : base(options, "", "", "", "", gen)
    {
      Contract.Requires(options != null);
      Contract.Requires(gen != null);
      Contract.Requires(ctx != null);
      InitializeGlobalInformation("UnivBackPred2.smt");

      this.ctx = ctx;

      TypeAxiomBuilder axBuilder;
      switch (CommandLineOptions.Clo.TypeEncodingMethod) {
        case CommandLineOptions.TypeEncoding.Arguments:
          axBuilder = new TypeAxiomBuilderArguments(gen);
          break;
        default:
          axBuilder = new TypeAxiomBuilderPremisses(gen);
          break;
      }
      axBuilder.Setup();
      AxBuilder = axBuilder;
      UniqueNamer namer = new UniqueNamer();
      Namer = namer;
      this.DeclCollector = new TypeDeclCollector(namer);

    }

    public override ProverContext Context
    {
      get
      {
        Contract.Ensures(Contract.Result<ProverContext>() != null);

        return ctx;
      }
    }

    private readonly TypeAxiomBuilder AxBuilder;
    private readonly UniqueNamer Namer;
    private readonly TypeDeclCollector DeclCollector;

    private void FeedTypeDeclsToProver()
    {
      foreach (string s in DeclCollector.GetNewDeclarations()) {
        Contract.Assert(s != null);
        AddTypeDecl(s);
      }
    }

    public override void BeginCheck(string descriptiveName, VCExpr vc, ErrorHandler handler)
    {
      //Contract.Requires(descriptiveName != null);
      //Contract.Requires(vc != null);
      //Contract.Requires(handler != null);
      TextWriter output = OpenOutputFile(descriptiveName);
      Contract.Assert(output != null);

      string name =
        MakeBenchmarkNameSafe(TPTPExprLineariser.MakeIdPrintable(descriptiveName));
      Contract.Assert(name != null);
      WriteLineAndLog(output, "(benchmark " + name);
      WriteLineAndLog(output, _backgroundPredicates);

      if (!AxiomsAreSetup) {
        AddAxiom(VCExpr2String(ctx.Axioms, -1));
        AxiomsAreSetup = true;
      }

      string vcString = ":formula (not " + VCExpr2String(vc, 1) + ")";
      string prelude = ctx.GetProverCommands(true);
      Contract.Assert(prelude != null);
      WriteLineAndLog(output, prelude);

      foreach (string s in TypeDecls) {
        Contract.Assert(s != null);
        WriteLineAndLog(output, s);
      }
      foreach (string s in Axioms) {
        Contract.Assert(s != null);
        WriteLineAndLog(output, ":assumption");
        WriteLineAndLog(output, s);
      }

      WriteLineAndLog(output, vcString);
      WriteLineAndLog(output, ")");

      output.Close();
    }

    // certain words that should not occur in the name of a benchmark
    // because some solvers don't like them
    private readonly static List<string/*!>!*/> BadBenchmarkWords = new List<string/*!*/>();
    static TPTPProcessTheoremProver()
    {
      BadBenchmarkWords.Add("Array"); BadBenchmarkWords.Add("Arrray");
    }

    private string MakeBenchmarkNameSafe(string name)
    {
      Contract.Requires(name != null);
      Contract.Ensures(Contract.Result<string>() != null);

      for (int i = 0; i < BadBenchmarkWords.Count; i = i + 2)
        name = name.Replace(BadBenchmarkWords[i], BadBenchmarkWords[i + 1]);
      return name;
    }

    private TextWriter OpenOutputFile(string descriptiveName)
    {
      Contract.Requires(descriptiveName != null);
      Contract.Ensures(Contract.Result<TextWriter>() != null);

      string filename = CommandLineOptions.Clo.SMTLibOutputPath;
      filename = Helpers.SubstituteAtPROC(descriptiveName, cce.NonNull(filename));
      return new StreamWriter(filename, false);
    }

    private void WriteLineAndLog(TextWriter output, string msg)
    {
      Contract.Requires(output != null);
      Contract.Requires(msg != null);
      LogActivity(msg);
      output.WriteLine(msg);
    }

    [NoDefaultContract]
    public override Outcome CheckOutcome(ErrorHandler handler)
    {  //Contract.Requires(handler != null);
      Contract.EnsuresOnThrow<UnexpectedProverOutputException>(true);
      return Outcome.Undetermined;
    }

    protected string VCExpr2String(VCExpr expr, int polarity)
    {
      Contract.Requires(expr != null);
      Contract.Ensures(Contract.Result<string>() != null);

      DateTime start = DateTime.Now;
      if (CommandLineOptions.Clo.Trace)
        Console.Write("Linearising ... ");

      // handle the types in the VCExpr
      TypeEraser eraser;
      switch (CommandLineOptions.Clo.TypeEncodingMethod) {
        case CommandLineOptions.TypeEncoding.Arguments:
          eraser = new TypeEraserArguments((TypeAxiomBuilderArguments)AxBuilder, gen);
          break;
        default:
          eraser = new TypeEraserPremisses((TypeAxiomBuilderPremisses)AxBuilder, gen);
          break;
      }
      Contract.Assert(eraser != null);
      VCExpr exprWithoutTypes = eraser.Erase(expr, polarity);
      Contract.Assert(exprWithoutTypes != null);

      LetBindingSorter letSorter = new LetBindingSorter(gen);
      Contract.Assert(letSorter != null);
      VCExpr sortedExpr = letSorter.Mutate(exprWithoutTypes, true);
      Contract.Assert(sortedExpr != null);
      VCExpr sortedAxioms = letSorter.Mutate(AxBuilder.GetNewAxioms(), true);
      Contract.Assert(sortedAxioms != null);

      DeclCollector.Collect(sortedAxioms);
      DeclCollector.Collect(sortedExpr);
      FeedTypeDeclsToProver();

      AddAxiom(TPTPExprLineariser.ToString(sortedAxioms, Namer));
      string res = TPTPExprLineariser.ToString(sortedExpr, Namer);
      Contract.Assert(res != null);

      if (CommandLineOptions.Clo.Trace) {
        DateTime end = DateTime.Now;
        TimeSpan elapsed = end - start;
        Console.WriteLine("finished   [{0} s]  ", elapsed.TotalSeconds);
      }
      return res;
    }

    // the list of all known axioms, where have to be included in each
    // verification condition
    private readonly List<string/*!>!*/> Axioms = new List<string/*!*/>();
    private bool AxiomsAreSetup = false;




    // similarly, a list of function/predicate declarations
    private readonly List<string/*!>!*/> TypeDecls = new List<string/*!*/>();

    protected void AddAxiom(string axiom)
    {
      Contract.Requires(axiom != null);
      Axioms.Add(axiom);
      //      if (thmProver != null) {
      //        LogActivity(":assume " + axiom);
      //        thmProver.AddAxioms(axiom);
      //      }
    }

    protected void AddTypeDecl(string decl)
    {
      Contract.Requires(decl != null);
      TypeDecls.Add(decl);
      //     if (thmProver != null) {
      //       LogActivity(decl);
      //       thmProver.Feed(decl, 0);
      //     }
    }

    ////////////////////////////////////////////////////////////////////////////

    private static string _backgroundPredicates;

    static void InitializeGlobalInformation(string backgroundPred)
    {
      Contract.Requires(backgroundPred != null);
      Contract.Ensures(_backgroundPredicates != null);
      //throws ProverException, System.IO.FileNotFoundException;
      if (_backgroundPredicates == null) {
        string codebaseString =
          cce.NonNull(Path.GetDirectoryName(cce.NonNull(System.Reflection.Assembly.GetExecutingAssembly().Location)));

        // Initialize '_backgroundPredicates'
        string univBackPredPath = Path.Combine(codebaseString, backgroundPred);
        using (StreamReader reader = new System.IO.StreamReader(univBackPredPath)) {
          _backgroundPredicates = reader.ReadToEnd();
        }
      }
    }
  }

  public class Factory : ProverFactory
  {

    public override object SpawnProver(ProverOptions options, object ctxt)
    {
      //Contract.Requires(ctxt != null);
      //Contract.Requires(options != null);
      Contract.Ensures(Contract.Result<object>() != null);

      return this.SpawnProver(options,
                              cce.NonNull((DeclFreeProverContext)ctxt).ExprGen,
                              cce.NonNull((DeclFreeProverContext)ctxt));
    }

    public override object NewProverContext(ProverOptions options)
    {
      //Contract.Requires(options != null);
      Contract.Ensures(Contract.Result<object>() != null);

      if (CommandLineOptions.Clo.BracketIdsInVC < 0) {
        CommandLineOptions.Clo.BracketIdsInVC = 0;
      }

      VCExpressionGenerator gen = new VCExpressionGenerator();
      List<string>/*!>!*/ proverCommands = new List<string/*!*/>();
      // TODO: what is supported?
      //      proverCommands.Add("all");
      //      proverCommands.Add("simplify");
      //      proverCommands.Add("simplifyLike");
      VCGenerationOptions genOptions = new VCGenerationOptions(proverCommands);
      Contract.Assert(genOptions != null);

      return new DeclFreeProverContext(gen, genOptions);
    }

    protected virtual TPTPProcessTheoremProver SpawnProver(ProverOptions options,
                                                              VCExpressionGenerator gen,
                                                              DeclFreeProverContext ctx)
    {
      Contract.Requires(options != null);
      Contract.Requires(gen != null);
      Contract.Requires(ctx != null);
      Contract.Ensures(Contract.Result<TPTPProcessTheoremProver>() != null);

      return new TPTPProcessTheoremProver(options, gen, ctx);
    }
  }
}