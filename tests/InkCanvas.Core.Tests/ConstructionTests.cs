using InkCanvas.AnalyticGeometry.Algebra;
using InkCanvas.AnalyticGeometry.Construction;
using System.IO.Compression;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ConstructionTests
{
    [Fact]
    public void DrawingCommandsFollowTheirSourceObjects()
    {
        var document = new ConstructionDocument();
        document.Execute("A=(0,0);B=(6,0);s=Segment(A,B);M=Midpoint(s);"
            + "c=Compass(s,A);C=Center(c);q=PerpendicularBisector(s);L=Measure(Length(s),{s},\"长度\")");
        var kernel = new ConstructionKernel();
        var values = kernel.Evaluate(document.State).Values;
        Assert.Equal(new GeoVector(3, 0), values["M"].Origin);
        Assert.Equal(new GeoVector(0, 0), values["C"].Origin);
        Assert.Equal(6, values["c"].Radius);
        Assert.Equal(6, values["L"].Number);
        Assert.Equal(values["M"].Origin, values["L"].TextAnchor);

        document.Execute("B=(8,0)");
        values = kernel.Evaluate(document.State).Values;
        Assert.Equal(new GeoVector(4, 0), values["q"].Origin);
        Assert.Equal(values["q"].Origin, values["M"].Origin);
        Assert.Equal(8, values["c"].Radius);
        Assert.Equal(values["M"].Origin, values["L"].TextAnchor);
        document.Execute("p=Parabola((0,2),Line((-2,0),(2,0)));P=Midpoint(p)");
        Assert.False(kernel.Evaluate(document.State).Values["P"].Defined);
    }

    [Fact]
    public void DrawingCommandsResolveMultiOutputConstructions()
    {
        var result = Evaluate("g=Line((0,0),(1,0));h=Line((0,0),(0,1));b=AngleBisector(g,h);"
            + "k=Parallel((0,4),g);m=AngleBisector(g,k);c=Circle((0,0),2);t=Tangent(g,c);"
            + "I=Intersect(g,c,(3,0));f(x)=x^2-4;J=Intersect(f,g);"
            + "p=Parabola((0,1),Line((0,-1),(1,-1)));u=Tangent(g,p);"
            + "e=Ellipse((-3,0),(3,0),(0,4));v=Tangent(g,e);d=Diameter(g,e)");
        var values = result.Values;
        Assert.Equal(2, values["b"].Items.Count);
        Assert.Equal(0, values["b"].Items[0].Direction.Dot(values["b"].Items[1].Direction), 10);
        Assert.Equal(new GeoVector(0, 2), values["m"].Origin);
        Assert.Equal(new[] { -2d, 2d }, values["t"].Items.Select(item => item.Origin.Y).Order());
        Assert.Equal(new GeoVector(2, 0), values["I"].Origin);
        Assert.Equal(new[] { -2d, 2d }, values["J"].Items.Select(item => Math.Round(item.Origin.X, 8)));
        Assert.Equal(new GeoVector(0, 0), Assert.Single(values["u"].Items).Origin);
        Assert.Equal(new[] { -4d, 4d }, values["v"].Items.Select(item => Math.Round(item.Origin.Y, 8)).Order());
        Assert.Equal(0, values["d"].Direction.X);
        var projection = new GeometryProjection(new(), 800, 600);
        Assert.Equal(2, projection.Paths(values["b"]).Count());
    }

    [Fact]
    public void DrawingCommandsFitPointsAndSampleAConstrainedDriver()
    {
        var result = Evaluate("s=Segment((-2,0),(2,0));P=PointOnObject(s,0.4);Q=Translate(P,Vector((0,2)));"
            + "c=Locus(Q,P);f=FitLine({(0,0),(1,2),(2,1)});u=Vector((1,3));v=VectorFromPoint(Q,u);"
            + "p=Polygon((0,0),(4,0),(4,4),(2,2),(0,4));a=Measure(Angle(p,4),{PolygonVertex(p,4)},\"角度\")");
        var path = Assert.Single(result.Values["c"].Paths);
        Assert.Equal(new GeoVector(-2, 2), path[0]);
        Assert.Equal(new GeoVector(2, 2), path[^1]);
        Assert.Equal(.5, result.Values["f"].Direction.Y, 10);
        Assert.Equal(result.Values["Q"].Origin, Assert.Single(result.Values["v"].Paths)[0]);
        Assert.Equal(new GeoVector(1, 3), result.Values["v"].Direction);
        Assert.Equal(new GeoVector(2, 2), result.Values["a"].TextAnchor);
        Assert.Equal(3 * Math.PI / 2, result.Values["a"].Number, 10);
    }

    private static ConstructionEvaluation Evaluate(string commands)
    {
        var document = new ConstructionDocument();
        document.Execute(commands);
        var result = new ConstructionKernel().Evaluate(document.State);
        Assert.All(result.Values, pair => Assert.True(pair.Value.Defined, pair.Key + " · " + pair.Value.Error));
        return result;
    }

    [Theory]
    [InlineData("Simplify((x^2-1)/(x-1))", "x + 1")]
    [InlineData("Derivative(x^3,x)", "3 * x ^ 2")]
    [InlineData("Integral(x^2,x,0,3)", "9")]
    [InlineData("Determinant([[1,2],[3,4]])", "-2")]
    public void SymbolicEngineComputesExactResults(string input, string expected)
    {
        var actual = ComputerAlgebra.Parse(ComputerAlgebra.Calculate(input).Exact);
        foreach (int x in new[] { 2, 3, 5 })
            Assert.Equal(ComputerAlgebra.Real(ComputerAlgebra.Parse(expected).Substitute("x", x)),
                ComputerAlgebra.Real(actual.Substitute("x", x)), 9);
    }

    [Fact]
    public void DependencyUpdatesAreIncrementalAndUndoRestoresConstruction()
    {
        var document = new ConstructionDocument();
        document.Execute("A=(0,0); B=(4,0); C=Midpoint(A,B); s=Segment(A,C); f(x)=x^2");
        var kernel = new ConstructionKernel();
        var first = kernel.Evaluate(document.State);
        Assert.Equal(2, first.Values["C"].Origin.X);
        Assert.Equal(0, kernel.Evaluate(document.State).EvaluatedObjects);
        document.BeginGesture();
        document.Execute("B=(8,0)");
        document.Execute("B=(10,0)");
        document.EndGesture();
        var next = kernel.Evaluate(document.State);
        Assert.Equal(5, next.Values["C"].Origin.X);
        Assert.Equal(3, next.EvaluatedObjects);
        document.Undo();
        Assert.Equal(2, kernel.Evaluate(document.State).Values["C"].Origin.X);
        document.Redo();
        Assert.Equal(5, kernel.Evaluate(document.State).Values["C"].Origin.X);
    }

    [Fact]
    public void DraggingOnePointReevaluatesOnlyItsDependencyBranch()
    {
        var document = new ConstructionDocument();
        document.Execute("A=(0,0);B=(4,0);M=Midpoint(A,B);s=Segment(A,M);Q=(9,7);r=Circle(Q,2)");
        var kernel = new ConstructionKernel();
        var first = kernel.Evaluate(document.State);
        document.Update("A", item => item with { Definition = "(2,0)" });

        var next = kernel.Evaluate(document.State);

        Assert.Equal(3, next.EvaluatedObjects);
        Assert.Same(first.Values["Q"], next.Values["Q"]);
        Assert.Same(first.Values["r"], next.Values["r"]);
        Assert.Equal(3, next.Values["M"].Origin.X);
    }

    [Fact]
    public void DependencyPlanDetectsLabelsAddedAndRemovedAfterFirstEvaluation()
    {
        var document = new ConstructionDocument();
        document.Execute("B=A");
        var kernel = new ConstructionKernel();
        kernel.Evaluate(document.State);
        document.Execute("A=(2,3)");
        Assert.Equal(new GeoVector(2, 3), kernel.Evaluate(document.State).Values["B"].Origin);
        document.Execute("Delete(A)");
        Assert.NotEqual(GeometryKind.Point, kernel.Evaluate(document.State).Values["B"].Kind);
    }

    [Fact]
    public void SymbolicProofAcceptsNamedPredicateAndAnAttachedPoint()
    {
        var result = Evaluate("A=(0,0);B=(4,2);s=Segment(A,B);C=PointOnObject(s,.25);claim=AreCollinear(A,B,C);p=Prove(claim)");
        Assert.Equal(GeometryKind.Boolean, result.Values["p"].Kind);
        Assert.Equal(1, result.Values["p"].Number);
    }

    [Theory]
    [InlineData("Line((0,-4),(0,4))", "AreEqual(Distance(A,P),3)")]
    [InlineData("Circle(B,3)", "AreEqual(Distance(B,P),3)")]
    public void SymbolicProofReducesCircleIntersectionConstraints(string second, string predicate)
    {
        var result = Evaluate($"A=(0,0);B=(4,0);c=Circle(A,3);g={second};P=Intersect(c,g,1);"
            + $"proof=ProveDetails({predicate});unknown=Prove(AreEqual(P,A))");
        Assert.True(result.Values["proof"].Kind == GeometryKind.Boolean, result.Values["proof"].Display);
        Assert.Equal(1, result.Values["proof"].Number);
        Assert.Contains("构造约束证明", result.Values["proof"].Display);
        Assert.Contains("交点存在条件", result.Values["proof"].Display);
        Assert.Equal(GeometryKind.Text, result.Values["unknown"].Kind);
    }

    [Fact]
    public void SymbolicProofEliminatesIntersectionConstraintsOfTwoConicEquations()
    {
        var result = Evaluate("a=ConicEquation(x^2+4*y^2=16);b=ConicEquation(4*x^2+y^2=16);"
            + "P=Intersect(a,b,2);proof=Prove(AreEqual(x(P)^2,y(P)^2));"
            + "otherBranch=Prove(AreEqual(x(P),y(P)))");
        Assert.True(result.Values["proof"].Kind == GeometryKind.Boolean, result.Values["proof"].Display);
        Assert.Equal(1, result.Values["proof"].Number);
        Assert.Equal(GeometryKind.Text, result.Values["otherBranch"].Kind);
    }

    [Fact]
    public void GeometricPredicatesAndParametricDerivativeUseTheNativeEngine()
    {
        var result = Evaluate("c=Conic(1,4,-4,0,0,0);p=PointIn(c,(1,0.5));inside=IsInRegion(p,c);"
            + "outside=IsInRegion((3,0),c);l=LineEquation(y=1);tangent=IsTangent(l,c);proof=Prove(IsTangent(l,c));"
            + "curve=Curve(2*t,t^2,t,0,4);slopes=ParametricDerivative(curve)");
        Assert.Equal(1, result.Values["inside"].Number);
        Assert.Equal(0, result.Values["outside"].Number);
        Assert.Equal(1, result.Values["tangent"].Number);
        Assert.True(result.Values["proof"].Kind == GeometryKind.Boolean, result.Values["proof"].Display);
        Assert.Equal(1, result.Values["proof"].Number);
        Assert.Equal(new GeoVector(8, 4), result.Values["slopes"].Paths.Single()[^1]);
    }

    [Fact]
    public void ConicIntersectionsFindFourCrossingsAndTangenciesOutsideTheView()
    {
        var result = Evaluate("a=ConicEquation((x-20)^2+4*(y-30)^2=16);"
            + "b=ConicEquation(4*(x-20)^2+(y-30)^2=16);hits=Intersect(a,b);"
            + "c=ConicEquation(y=x^2);d=ConicEquation(y=-x^2);touch=Intersect(c,d)");
        Assert.Equal(4, result.Values["hits"].Items.Count);
        Assert.All(result.Values["hits"].Items, item =>
        {
            Assert.Equal(4 / Math.Sqrt(5), Math.Abs(item.Origin.X - 20), 6);
            Assert.Equal(4 / Math.Sqrt(5), Math.Abs(item.Origin.Y - 30), 6);
        });
        Assert.Equal(new GeoVector(0, 0), Assert.Single(result.Values["touch"].Items).Origin);
    }

    [Fact]
    public void CircleAndConicOverloadsPreserveSpatialGeometryAndAnalyticFeatures()
    {
        var result = Evaluate("A=(0,0,0);B=(0,0,3);g=Line(A,B);p=(2,0,3);c=Circle(g,p);"
            + "d=Circle(B,2,xOyPlane);k=Compass(A,p,B);ellipse=Conic({1,4,-4,0,0,0});"
            + "parabola=ConicEquation(y=x^2);focusPoint=Focus(parabola);"
            + "hyperbola=Conic(1,-1,-1,0,0,0);h=Line((-3,0),(3,0));crossings=Intersect(h,ellipse)");
        Assert.Equal(new GeoVector(0, 0, 3), result.Values["c"].Origin);
        Assert.Equal(new GeoVector(0, 0, 1), result.Values["d"].Normal);
        Assert.Equal(Math.Sqrt(13), result.Values["k"].Radius, 10);
        Assert.Equal(2, result.Values["ellipse"].Radius, 10);
        Assert.Equal(1, result.Values["ellipse"].Number, 10);
        Assert.Equal("hyperbola", result.Values["hyperbola"].Expression);
        Assert.Equal(0.25, result.Values["focusPoint"].Origin.Y, 10);
        Assert.Equal(new[] { -2d, 2d }, result.Values["crossings"].Items.Select(item => item.Origin.X).Order());
    }

    [Fact]
    public void GeoGebraPlaneOverloadsPreserveSpatialOrientation()
    {
        var result = Evaluate("A=(1,0,0);B=(1,2,0);C=(1,0,2);T=Polygon(A,B,C);p=Plane(T);q=Plane(C,p);"
            + "u=Vector(A,B);v=Vector(A,C);r=Plane(A,u,v);g=Line(A,B);h=Line(B,C);s=Plane(g,h);"
            + "c=Circle((0,0),2);t=Plane(c)");
        foreach (var label in new[] { "p", "q", "r", "s" })
        {
            Assert.Equal(GeometryKind.Plane, result.Values[label].Kind);
            Assert.Equal(1, Math.Abs(result.Values[label].Normal.X), 10);
            Assert.Equal(1, result.Values[label].Origin.X, 10);
        }
        Assert.Equal(new GeoVector(0, 0, 1), result.Values["t"].Normal);
    }

    [Fact]
    public void SpatialProofVerifiesCoplanarityCollinearityAndEqualLengths()
    {
        var result = Evaluate("A=(0,0,0);B=(2,0,1);C=(1,2,3);s=Segment(A,B);D=Midpoint(s);"
            + "E=Midpoint(A,C);F=Midpoint(B,C);g=Segment(D,E);h=Segment(B,C);"
            + "coplanar=Prove(AreCoplanar(A,B,C,F));collinear=Prove(AreCollinear(A,B,D));"
            + "parallel=Prove(AreParallel(g,h));length=Prove(Distance(A,D)==Distance(D,B));"
            + "free=(0,0,0);counterexample=Prove(AreCoplanar(A,B,C,free))");
        foreach (string label in new[] { "coplanar", "collinear", "parallel", "length", "counterexample" })
            Assert.True(result.Values[label].Kind == GeometryKind.Boolean, result.Values[label].Display);
        Assert.Equal(1, result.Values["coplanar"].Number);
        Assert.Equal(1, result.Values["collinear"].Number);
        Assert.Equal(1, result.Values["parallel"].Number);
        Assert.Equal(1, result.Values["length"].Number);
        Assert.Equal(0, result.Values["counterexample"].Number);
    }

    [Fact]
    public void SymbolicProofEstablishesConcurrencyOfTriangleMedians()
    {
        var result = Evaluate("A=(0,0);B=(8,0);C=(2,6);D=Midpoint(B,C);E=Midpoint(A,C);F=Midpoint(A,B);"
            + "g=Line(A,D);h=Line(B,E);j=Line(C,F);claim=AreConcurrent(g,h,j);p=Prove(claim)");
        Assert.Equal(1, result.Values["p"].Number);
    }

    [Fact]
    public void SymbolicTriangleCentroidLiesOnEachMedian()
    {
        var result = Evaluate("A=(0,0,1);B=(9,0,0);C=(0,6,2);triangle=Polygon(A,B,C);G=Centroid(triangle);"
            + "F=Midpoint(B,C);p=Prove(AreCollinear(A,G,F));c=Circle((1,2),4);r=Radius(c)");
        Assert.True((new GeoVector(3, 2, 1) - result.Values["G"].Origin).Length < 1e-12);
        Assert.Equal(1, result.Values["p"].Number);
        Assert.Equal(4, result.Values["r"].Number);
    }

    [Fact]
    public void GestureEvaluationUsesCoarseCurvesAndRestoresFinalQuality()
    {
        var document = new ConstructionDocument();
        document.Execute("f(x)=sin(20*x);g=Curve(t,t^2,t,0,1);h=x^2+y^2=16");
        var kernel = new ConstructionKernel();

        var interactive = kernel.Evaluate(document.State, interactive: true);
        var final = kernel.Evaluate(document.State);

        Assert.Equal(129, interactive.Values["f"].Paths[0].Length);
        Assert.Equal(129, interactive.Values["g"].Paths[0].Length);
        int interactiveImplicitPoints = interactive.Values["h"].Paths.Sum(path => path.Length);
        Assert.Equal(513, final.Values["f"].Paths[0].Length);
        Assert.Equal(513, final.Values["g"].Paths[0].Length);
        Assert.True(final.Values["h"].Paths.Sum(path => path.Length) > interactiveImplicitPoints);
        Assert.Equal(3, interactive.EvaluatedObjects);
        Assert.Equal(3, final.EvaluatedObjects);
    }

    [Fact]
    public void DraggingKeepsSettledSamplesForUnchangedGeometry()
    {
        var document = new ConstructionDocument();
        document.Execute("a=1;f(x)=sin(x);g=Curve(t,a*t^2,t,0,1)");
        var kernel = new ConstructionKernel();
        var settled = kernel.Evaluate(document.State);

        document.Execute("a=2");
        var dragging = kernel.Evaluate(document.State, interactive: true);
        Assert.Same(settled.Values["f"], dragging.Values["f"]);
        Assert.Equal(129, dragging.Values["g"].Paths[0].Length);
        Assert.Equal(2, dragging.EvaluatedObjects);

        var released = kernel.Evaluate(document.State);
        Assert.Same(settled.Values["f"], released.Values["f"]);
        Assert.Equal(513, released.Values["g"].Paths[0].Length);
        Assert.Equal(1, released.EvaluatedObjects);
    }

    [Theory]
    [InlineData("Gamma(5)", 24)]
    [InlineData("Erf(0)", 0)]
    [InlineData("Erfc(0)", 1)]
    [InlineData("1+Gamma(2)", 2)]
    [InlineData("Zeta(2)", 1.6449340668482264)]
    [InlineData("Beta(2,3)", 1d / 12)]
    [InlineData("GammaRegularized(1,1)", .6321205588285577)]
    [InlineData("Gamma(1,1)", .6321205588285577)]
    [InlineData("LambertW(e,0)", 1)]
    [InlineData("LambertW(-2/e^2,-1)", -2)]
    [InlineData("SinIntegral(0)", 0)]
    [InlineData("CosIntegral(1)", .3374039229009681)]
    [InlineData("ExpIntegral(1)", 1.8951178163559368)]
    [InlineData("Psi(1)", -.5772156649015329)]
    [InlineData("Polygamma(1,1)", 1.6449340668482264)]
    [InlineData("Beta(2,3,1/2)", .057291666666666664)]
    [InlineData("BetaRegularized(2,3,1/2)", .6875)]
    [InlineData("nPr(5,2)", 20)]
    [InlineData("round(2.34567,3)", 2.346)]
    [InlineData("nroot(-32,5)", -2)]
    [InlineData("cbrt(-27)", -3)]
    [InlineData("log₂(8)+log₁₀(1000)", 6)]
    [InlineData("cosec(pi/6)+cotan(pi/4)", 3)]
    [InlineData("sech(0)+arcsinh(0)+arccosh(1)+arctanh(0)", 1)]
    [InlineData("BetaRegularized(0.5,0.5,0)+BetaRegularized(0.5,0.5,1)", 1)]
    [InlineData("GammaRegularized(0.5,0)", 0)]
    [InlineData("atan2(1,-1)", 3 * Math.PI / 4)]
    [InlineData("atan2d(-1,-1)", -135)]
    [InlineData("atan2(0,-1)", Math.PI)]
    [InlineData("atan2(2,0)", Math.PI / 2)]
    [InlineData("ld(32)+lg(100)", 7)]
    public void SymbolicEngineEvaluatesBuiltInSpecialFunctions(string expression, double expected)
        => Assert.Equal(expected, ComputerAlgebra.Real(ComputerAlgebra.Parse(expression)), 10);

    [Theory]
    [InlineData("f(x)=Erf(x)")]
    [InlineData("f=Function(BetaRegularized(1.5,2.5,x),x,0,1)")]
    [InlineData("f=Function(GammaRegularized(2,x),x,0,5)")]
    [InlineData("f(x)=atan2(x,1)")]
    public void SpecialFunctionsCanDriveDynamicGeometry(string definition)
    {
        var result = Evaluate(definition);

        Assert.Equal(GeometryKind.Function, result.Values["f"].Kind);
        Assert.NotEmpty(result.Values["f"].Paths);
    }

    [Fact]
    public void NestedFunctionCallsResolveRightToLeftAndFollowParameterChanges()
    {
        var document = new ConstructionDocument();
        document.Execute("a=2; f(x)=x+a; g(x)=2*x; value=f(g(f(3)))");
        var kernel = new ConstructionKernel();
        Assert.Equal(12, kernel.Evaluate(document.State).Values["value"].Number);
        document.Execute("a=3");
        Assert.Equal(15, kernel.Evaluate(document.State).Values["value"].Number);
    }

    [Fact]
    public void GeoGebraListOverloadsSupportMatricesAndWeightedValues()
    {
        var result = Evaluate("list={1,2,4}; weights={1,1,2}; mat={{1,2},{3,4}}; entry=Element(mat,2,1); " +
            "n=Length(list); s=Sum(list,2); t=Sum(list,weights); average=Mean(list,weights); p=Sum({(1,2),(2,3)}); q=Sum({\"x\",\"y\"})");
        Assert.Equal(3, result.Values["entry"].Number);
        Assert.Equal(3, result.Values["n"].Number);
        Assert.Equal(3, result.Values["s"].Number);
        Assert.Equal(11, result.Values["t"].Number);
        Assert.Equal(2.75, result.Values["average"].Number);
        Assert.Equal(new GeoVector(3, 5), result.Values["p"].Origin);
        Assert.Equal("xy", result.Values["q"].Display);
    }

    [Fact]
    public void GeoGebraCalculusOverloadsResolveDerivativeOrderBoundsAndInitialGuess()
    {
        var result = Evaluate("f(t)=t^3; d=Derivative(f,2); value=d(2); lo=1; hi=2; area=Integral(f,lo,hi); " +
            "p=2*area; g(x)=(x-5)*(x-10); r=Root(g,8,13); s=Root(g,6)");
        Assert.Equal(12, result.Values["value"].Number);
        Assert.Equal(3.75, result.Values["area"].Number, 8);
        Assert.Equal(7.5, result.Values["p"].Number, 8);
        Assert.Equal(10, result.Values["r"].Origin.X, 7);
        Assert.Equal(5, result.Values["s"].Origin.X, 7);
    }

    [Fact]
    public void ChangingOnlyTheViewDoesNotRaiseEvaluationChange()
    {
        var document = new ConstructionDocument();
        document.Execute("A=(1,2);f(x)=x^2;g=Segment(A,(4,2))");
        int evaluations = 0, views = 0;
        document.EvaluationChanged += () => evaluations++;
        document.ViewChanged += () => views++;
        var kernel = new ConstructionKernel();
        var initial = kernel.Evaluate(document.State);

        document.SetView(document.State with { XMin = -20, XMax = 20 });
        var updated = kernel.Evaluate(document.State);

        Assert.Equal(0, evaluations);
        Assert.Equal(1, views);
        Assert.Equal(1, updated.EvaluatedObjects);
        Assert.Same(initial.Values["A"], updated.Values["A"]);
        Assert.Same(initial.Values["g"], updated.Values["g"]);
    }

    [Fact]
    public void GeoGebraArchiveImportsViewCommandsAndStyles()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ggb");
        const string xml = """
            <geogebra><euclidianView><size width="1000" height="750"/><coordSystem xZero="500" yZero="375" scale="50" yscale="50"/><evSettings axes="true" grid="true"/></euclidianView><construction>
              <expression label="A" exp="(1, 2)" type="point"/><element type="point" label="A"><show object="true" label="true"/><objColor r="0.7843137254901961" g="0.0784313725490196" b="0.1568627450980392" alpha="1"/><coords x="1" y="2" z="1"/></element>
              <expression label="B" exp="(5, 2)" type="point"/><element type="point" label="B"><show object="true" label="true"/><objColor r="30" g="40" b="50" alpha="255"/><coords x="5" y="2" z="1"/></element>
              <command name="UnknownConstruction"><input a0="A"/><output a0="C"/></command>
              <element type="point" label="C"><coords x="4" y="6" z="2"/></element>
              <element type="line" label="l"><line x="0" y="1" z="-2"/></element>
              <command name="Midpoint"><input a0="A" a1="B"/><output a0="M"/></command>
              <element type="point" label="M"><show object="true" label="true"/></element>
              <command name="Segment"><input a0="A" a1="M"/><output a0="s"/></command>
              <element type="segment" label="s"><show object="true" label="false"/><lineStyle thickness="4"/></element>
              <expression label="a" exp="2"/><element type="numeric" label="a"/>
              <expression label="f" exp="f(x) = a * x"/><element type="function" label="f"/>
              <expression label="h" exp="h(x) = f(x) + 1"/><element type="function" label="h"><condition showObject="showCurve"/></element>
              <element type="boolean" label="showCurve"><value val="false"/></element>
              <expression label="surface" exp="surface(u,v) = a * u + v"/><element type="functionNVar" label="surface"/>
            </construction></geogebra>
            """;
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("geogebra.xml").Open())) writer.Write(xml);
            var imported = GeoGebraImport.Load(path);
            var document = new ConstructionDocument(); document.Load(imported.Snapshot);
            var kernel = new ConstructionKernel();
            var result = kernel.Evaluate(document.State);

            Assert.Equal(-10, imported.Snapshot.XMin);
            Assert.Equal(10, imported.Snapshot.XMax);
            Assert.Equal(new GeoVector(3, 2), result.Values["M"].Origin);
            Assert.Equal(0xFFC81428u, document.State.Objects.Single(item => item.Label == "A").Color);
            Assert.Equal(4, document.State.Objects.Single(item => item.Label == "s").LineWidth);
            Assert.Equal(new GeoVector(2, 3), result.Values["C"].Origin);
            Assert.Equal(2, result.Values["l"].Origin.Y);
            Assert.Equal(0xFF1E2832u, document.State.Objects.Single(item => item.Label == "B").Color);
            Assert.Equal(2, imported.Warnings.Count);
            Assert.Equal(GeometryKind.Function, result.Values["h"].Kind);
            Assert.Equal(21, result.Values["h"].Paths[0][^1].Y, 10);
            Assert.NotEmpty(result.Values["surface"].Faces);
            var curve = document.State.Objects.Single(item => item.Label == "h");
            Assert.False(result.IsVisible(curve));
            string hiddenSvg = ConstructionExport.Svg(document.State, result);
            document.Execute("Rename(showCurve,displayCurve); displayCurve=Checkbox(true)");
            var visible = kernel.Evaluate(document.State);
            Assert.True(visible.IsVisible(document.State.Objects.Single(item => item.Label == "h")));
            Assert.Same(result.Values["h"], visible.Values["h"]);
            Assert.NotEqual(hiddenSvg, ConstructionExport.Svg(document.State, visible));
            document.Execute("a=3");
            var edited = kernel.Evaluate(document.State);
            Assert.Equal(31, edited.Values["h"].Paths[0][^1].Y, 10);
            Assert.NotEqual(result.Values["surface"].Faces[0], edited.Values["surface"].Faces[0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("Cube", 8, 6, 12)]
    [InlineData("Tetrahedron", 4, 4, 6)]
    [InlineData("Octahedron", 6, 8, 12)]
    [InlineData("Icosahedron", 12, 20, 30)]
    [InlineData("Dodecahedron", 20, 12, 30)]
    public void GeoGebraRegularSolidOutputsFollowTheirInputEdge(string command, int vertices, int faces, int edges)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ggb");
        string xml = $"""
            <geogebra><construction>
              <element type="point3d" label="A"><coords x="0" y="0" z="0" w="1"/></element>
              <element type="point3d" label="B"><coords x="2" y="0" z="0" w="1"/></element>
              <command name="{command}"><input a0="A" a1="B" a2="xOyPlane"/>
                <output a0="solid" a1="C" a2="baseFace" a3="edgeAB"/></command>
              <element type="polyhedron" label="solid"/><element type="point3d" label="C"/>
              <element type="polygon3d" label="baseFace"/><element type="segment3d" label="edgeAB"/>
            </construction></geogebra>
            """;
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("geogebra.xml").Open()))
                writer.Write(xml);
            var imported = GeoGebraImport.Load(path);
            Assert.Empty(imported.Warnings);
            var document = new ConstructionDocument();
            document.Load(imported.Snapshot);
            var kernel = new ConstructionKernel();
            var initial = kernel.Evaluate(document.State).Values;
            Assert.All(initial, pair => Assert.True(pair.Value.Defined, pair.Value.Error));
            Assert.Equal(vertices, initial["solid"].Points.Count);
            Assert.Equal(faces, initial["solid"].FaceLoops.Count);
            Assert.Equal(edges, initial["solid"].Paths.Count);
            Assert.All(initial["solid"].Paths, edge => Assert.Equal(2, (edge[0] - edge[1]).Length, 10));
            Assert.Contains(new GeoVector(2, 0, 0), initial["edgeAB"].Paths.Single());
            Assert.Contains(initial["C"].Origin, initial["baseFace"].Points);
            Assert.Equal(0, initial["C"].Origin.Z, 10);

            document.Update("B", item => item with { Definition = "(0,4,0)" });
            var edited = kernel.Evaluate(document.State).Values;
            Assert.All(edited, pair => Assert.True(pair.Value.Defined, pair.Value.Error));
            Assert.Equal(-2 * initial["C"].Origin.Y, edited["C"].Origin.X, 10);
            Assert.Equal(2 * initial["C"].Origin.X, edited["C"].Origin.Y, 10);
            Assert.Contains(new GeoVector(0, 4, 0), edited["edgeAB"].Paths.Single());
            Assert.Contains(edited["C"].Origin, edited["baseFace"].Points);
            Assert.Equal(8 * initial["solid"].Number, edited["solid"].Number, 10);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParametricCurveDerivativeRetainsItsDomainAndDynamicDependencies()
    {
        var document = new ConstructionDocument();
        document.Execute("a=3; order=2; c=Curve(a*p^2,p^3,p^4,p,1,2); d=CurveDerivative(c,order); e=Derivative(d); f=Derivative(Translate(c,(10,20,30)),2)");
        var kernel = new ConstructionKernel();
        var initial = kernel.Evaluate(document.State).Values;
        Assert.All(initial, pair => Assert.True(pair.Value.Defined, pair.Value.Error));
        Assert.Equal(new GeoVector(6, 6, 12), initial["d"].Paths.Single()[0]);
        Assert.Equal(new GeoVector(6, 12, 48), initial["d"].Paths.Single()[^1]);
        Assert.Equal(new GeoVector(0, 6, 24), initial["e"].Paths.Single()[0]);
        Assert.Equal(initial["d"].Paths.Single(), initial["f"].Paths.Single());

        document.Update("a", item => item with { Definition = "5" });
        document.Update("order", item => item with { Definition = "1" });
        var edited = kernel.Evaluate(document.State).Values;
        Assert.All(edited, pair => Assert.True(pair.Value.Defined, pair.Value.Error));
        Assert.Equal(new GeoVector(10, 3, 4), edited["d"].Paths.Single()[0]);
    }

    [Fact]
    public void GeoGebraGeneratedOutputsAndObjectScriptsRemainEditable()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ggb");
        const string xml = """
            <geogebra><construction>
              <element type="point3d" label="A"><coords x="0" y="0" z="0" w="1"/></element>
              <element type="point3d" label="B"><coords x="2" y="0" z="0" w="1"/></element>
              <element type="point3d" label="C"><coords x="0" y="2" z="0" w="1"/></element>
              <element type="point3d" label="D"><coords x="0" y="0" z="3" w="1"/></element>
              <command name="Prism"><input a0="A" a1="B" a2="C" a3="D"/><output a0="p" a1="E" a2="F"/></command>
              <element type="point3d" label="E"><coords x="2" y="0" z="3" w="1"/></element>
              <element type="point3d" label="F"><coords x="0" y="2" z="3" w="1"/></element>
              <command name="Pyramid"><input a0="A" a1="B" a2="C" a3="D"/><output a0="q" a1="edgeDB"/></command>
              <element type="segment3d" label="edgeDB"/>
              <command name="Polygon"><input a0="A" a1="B" a2="4"/><output a0="square" a1="side" a2="Q"/></command>
              <element type="segment" label="side"/>
              <element type="point" label="Q"><coords x="2" y="2" z="1"/></element>
              <element type="numeric" label="n"><value val="1"/><javascript onUpdate="ggbApplet.setVisible('next', true);"/></element>
              <element type="button" label="next"><caption val="Next ("/><ggbscript val="SetValue(n,n+1)"/></element>
              <expression label="l" exp="y=n*x+2"/><element type="line" label="l"/>
              <element type="vector" label="v"><coords x="0" y="1" z="0"/></element>
              <command name="Line"><input a0="A" a1="v"/><output a0="m"/></command>
              <element type="line" label="m"/>
              <command name="Intersect"><input a0="l" a1="m"/><output a0="I"/></command>
              <element type="point" label="I"/>
              <command name="Circle"><input a0="A" a1="n"/><output a0="c"/></command>
              <element type="conic" label="c"/>
              <command name="PointIn"><input a0="c"/><output a0="inside"/></command>
              <element type="point" label="inside"><coords x="0.5" y="0.5" z="1"/></element>
            </construction></geogebra>
            """;
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("geogebra.xml").Open())) writer.Write(xml);
                using var init = new StreamWriter(archive.CreateEntry("geogebra_javascript.js").Open());
                init.WriteLine("function ggbOnInit() {}");
            }

            var imported = GeoGebraImport.Load(path);
            Assert.Empty(imported.Warnings);
            var document = new ConstructionDocument();
            document.Load(imported.Snapshot);
            var kernel = new ConstructionKernel();
            var initial = kernel.Evaluate(document.State);
            Assert.Equal(new GeoVector(2, 0, 3), initial.Values["E"].Origin);
            Assert.Equal(new GeoVector(0, 2, 3), initial.Values["F"].Origin);
            Assert.Equal("Next (", initial.Values["next"].Display);
            Assert.Equal(GeometryKind.Line, initial.Values["l"].Kind);
            Assert.Equal(GeometryKind.Vector, initial.Values["v"].Kind);
            Assert.Equal(new GeoVector(0, 2), initial.Values["I"].Origin);
            Assert.Equal(new GeoVector(.5, .5), initial.Values["inside"].Origin);
            Assert.True(ConstructionDocument.IsRegionPoint(document.State.Objects.Single(item => item.Label == "inside")));
            Assert.StartsWith("js:", document.State.Objects.Single(item => item.Label == "n").OnUpdate);

            document.Update("B", item => item with { Definition = "(4,0,0)" });
            document.Update("D", item => item with { Definition = "(0,0,5)" });
            document.RunScript(document.State.Objects.Single(item => item.Label == "next").OnClick);
            var edited = kernel.Evaluate(document.State);
            Assert.Equal(new GeoVector(4, 0, 5), edited.Values["E"].Origin);
            Assert.Equal(new GeoVector(0, 2, 5), edited.Values["F"].Origin);
            Assert.Contains(new GeoVector(0, 0, 5), edited.Values["edgeDB"].Paths.Single());
            Assert.Contains(new GeoVector(4, 0, 0), edited.Values["edgeDB"].Paths.Single());
            Assert.Equal(4, edited.Values["side"].Direction.Length, 10);
            Assert.Equal(4, edited.Values["Q"].Origin.X, 10);
            Assert.Equal(4, edited.Values["Q"].Origin.Y, 10);
            Assert.Equal(2, edited.Values["n"].Number);
            Assert.Equal(2, edited.Values["l"].Direction.Y / edited.Values["l"].Direction.X, 10);
            Assert.Equal(new GeoVector(1, 1), edited.Values["inside"].Origin);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GeoGebraDynamicTextUsesItsImportedPointAnchorAndExportsItsContent()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ggb");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("geogebra.xml").Open()))
                writer.Write("""
                    <geogebra><construction>
                      <element type="point" label="A"><coords x="1" y="2" z="1"/></element>
                      <element type="numeric" label="n"><value val="2"/></element>
                      <expression label="caption" exp="(&quot;Value &amp; = &quot; + n)"/>
                      <element type="text" label="caption"><startPoint exp="A"/></element>
                    </construction></geogebra>
                    """);
            var imported = GeoGebraImport.Load(path);
            Assert.Empty(imported.Warnings);
            var document = new ConstructionDocument();
            document.Load(imported.Snapshot);
            var kernel = new ConstructionKernel();
            var initial = kernel.Evaluate(document.State);
            Assert.Equal("Value & = 2", initial.Values["caption"].Display);
            Assert.Equal(new GeoVector(1, 2), initial.Values["caption"].TextAnchor);
            Assert.Contains("Value &amp; = 2", ConstructionExport.Svg(document.State, initial));

            document.Execute("A=(3,4); n=5");
            var edited = kernel.Evaluate(document.State);
            Assert.Equal("Value & = 5", edited.Values["caption"].Display);
            Assert.Equal(new GeoVector(3, 4), edited.Values["caption"].TextAnchor);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GeoGebraListOperationsComposeWithCellRangesAndWeightedStatistics()
    {
        var result = Evaluate("A1=1;A2=2;A3=3;M=Flatten({A1:A3,{3,1}});"
            + "order=Sort({4,5,6},{2,3,1});unique=Unique(M);middle=Take(Reverse(M),2,3);"
            + "spread=SD(A1:A3,{1,2,1});med=Median(A1:A3,{1,2,1});prod=Product(k,k,1,5);"
            + "decreasing=Sequence(A3,A1);selected=First(\"甲乙丙\",2)");
        static double[] Numbers(GeometryValue value) => value.Items.Select(item => item.Number).ToArray();
        Assert.Equal(new double[] { 1, 2, 3, 3, 1 }, Numbers(result.Values["M"]));
        Assert.Equal(new double[] { 6, 4, 5 }, Numbers(result.Values["order"]));
        Assert.Equal(new double[] { 1, 2, 3 }, Numbers(result.Values["unique"]));
        Assert.Equal(new double[] { 3, 3 }, Numbers(result.Values["middle"]));
        Assert.Equal(Math.Sqrt(.5), result.Values["spread"].Number, 10);
        Assert.Equal(2, result.Values["med"].Number);
        Assert.Equal(120, result.Values["prod"].Number);
        Assert.Equal(new double[] { 3, 2, 1 }, Numbers(result.Values["decreasing"]));
        Assert.Equal("甲乙", result.Values["selected"].Display);
    }

    [Fact]
    public void SpreadsheetRangesTrackInteriorCellsAndSkipMissingCells()
    {
        var document = new ConstructionDocument();
        document.Execute("A1=1; A3=3; B1=4; B3=6; L=CellRange($B$3,$A$1,\"numeric\"); n=Sum(A1:B3); copy=$A$3");
        var kernel = new ConstructionKernel();
        var initial = kernel.Evaluate(document.State).Values;
        Assert.All(initial, pair => Assert.True(pair.Value.Defined, pair.Value.Error));
        Assert.Equal(new[] { 1d, 3d, 4d, 6d }, initial["L"].Items.Select(value => value.Number));
        Assert.Equal(14, initial["n"].Number);
        Assert.Equal(3, initial["copy"].Number);
        Assert.False(ConstructionKernel.DependsOn(document.State, "n", "B2"));

        document.Execute("B2=5");
        Assert.True(ConstructionKernel.DependsOn(document.State, "n", "B2"));
        Assert.Equal(19, kernel.Evaluate(document.State).Values["n"].Number);
        document.Execute("A3=30");
        Assert.Equal(46, kernel.Evaluate(document.State).Values["n"].Number);
        document.Execute("Delete(B2)");
        Assert.Equal(41, kernel.Evaluate(document.State).Values["n"].Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeoGebraEmbeddedToolOutputsRemainLinkedAfterTheInputChanges(bool nested)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ggb");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("geogebra.xml").Open()))
                    writer.Write("""
                        <geogebra><construction>
                          <element type="point" label="A"><coords x="0" y="0" z="1"/></element>
                          <element type="point" label="B"><coords x="2" y="0" z="1"/></element>
                          <command name="DiameterTool"><input a0="A" a1="B"/><output a0="c" a1="M"/></command>
                          <element type="conic" label="c"/><element type="point" label="M"/>
                          <command name="Circumference"><input a0="c"/><output a0="p"/></command>
                          <element type="numeric" label="p"/>
                        </construction></geogebra>
                        """);
                using var tools = new StreamWriter(archive.CreateEntry("geogebra_macro.xml").Open());
                string center = nested ? "CenterTool" : "Midpoint";
                tools.Write($"""
                    <geogebra><macro cmdName="DiameterTool">
                        <macroInput a0="U" a1="V"/><macroOutput a0="q" a1="W"/>
                        <construction>
                          <command name="{center}"><input a0="U" a1="V"/><output a0="W"/></command>
                          <element type="point" label="W"/>
                          <command name="Circle"><input a0="W" a1="U"/><output a0="q"/></command>
                          <element type="conic" label="q"/>
                        </construction>
                      </macro>
                    """);
                if (nested) tools.Write("""
                    <macro cmdName="CenterTool">
                      <macroInput a0="X" a1="Y"/><macroOutput a0="CenterPoint"/>
                      <construction>
                        <command name="Midpoint"><input a0="X" a1="Y"/><output a0="CenterPoint"/></command>
                      </construction>
                    </macro>
                    """);
                tools.Write("</geogebra>");
            }
            var imported = GeoGebraImport.Load(path);
            Assert.Empty(imported.Warnings);
            Assert.Equal(nested ? 2 : 1, imported.Snapshot.Tools.Count);
            Assert.Contains(imported.Snapshot.Tools, tool => tool.Name == "DiameterTool");
            var document = new ConstructionDocument();
            document.Load(imported.Snapshot);
            var kernel = new ConstructionKernel();
            var initial = kernel.Evaluate(document.State).Values;
            Assert.All(initial, pair => Assert.True(pair.Value.Defined, pair.Value.Error));
            Assert.Equal(1, initial["c"].Radius, 10);
            Assert.Equal(new GeoVector(1, 0), initial["M"].Origin);

            document.Update("B", item => item with { Definition = "(0,6)" });
            var edited = kernel.Evaluate(document.State).Values;
            Assert.All(edited, pair => Assert.True(pair.Value.Defined, pair.Value.Error));
            Assert.Equal(3, edited["c"].Radius, 10);
            Assert.Equal(6 * Math.PI, edited["p"].Number, 10);
            Assert.Equal(new GeoVector(0, 3), edited["M"].Origin);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GeoGebraClientReceivesHistoryAndExportWithoutConsumingRedo()
    {
        var document = new ConstructionDocument();
        document.Execute("n=0");
        document.Load(document.State with
        {
            AppletScript = """
                function ggbOnInit() {
                    ggbApplet.registerClientListener(function(event) {
                        if (event.type === 'undo') ggbApplet.setValue('n', 1);
                        if (event.type === 'redo') ggbApplet.setValue('n', 2);
                        if (event.type === 'export' && JSON.parse(event.argument)[0] === 'gif') ggbApplet.setValue('n', 3);
                        if (event.type === 'viewChanged2D' && event.viewNo === 1) ggbApplet.setValue('n', event.xscale);
                    });
                }
                """
        });
        document.InitializeApplet();
        document.HistoryRestored += name =>
        {
            var transaction = new ConstructionDocument();
            transaction.Load(document.State);
            transaction.RunAppletEvent(name, "", "");
            document.ApplyEventResult(transaction.State);
        };
        document.Execute("P=(1,2)");
        document.Undo();
        Assert.True(document.CanRedo);
        Assert.Contains(document.State.Objects, item => item.Label == "n" && item.Definition == "1");
        document.Redo();
        Assert.Contains(document.State.Objects, item => item.Label == "P");
        Assert.Contains(document.State.Objects, item => item.Label == "n" && item.Definition == "2");
        document.RunAppletEvent("OnExport", "", "", """["gif"]""");
        Assert.Contains(document.State.Objects, item => item.Label == "n" && item.Definition == "3");
        document.RunAppletEvent("OnViewChanged", "", "", fields: new Dictionary<string, double> { ["viewNo"] = 1, ["xscale"] = 40 });
        Assert.Contains(document.State.Objects, item => item.Label == "n" && item.Definition == "40");
    }

    [Fact]
    public void GeoGebraAppletRegistersAndRemovesListenersAndKeepsObjectIdentity()
    {
        var document = new ConstructionDocument();
        document.Execute("n=0;a=0;b=0;A=(0,0,0)");
        document.Load(document.State with
        {
            AppletScript = """
                ggbApplet.setValue('n', ggbApplet.getValue('n') + 1);
                function markAdd(label) {
                    if (label === 'P') ggbApplet.setValue('a', 2);
                }
                function markClick(label) {
                    if (label === 'P') ggbApplet.setCoords(label, 1, 2, 3);
                }
                function ggbOnInit(name, api) {
                    if (name !== 'ggbApplet') throw new Error('Unexpected applet name');
                    api.registerAddListener(markAdd);
                    api.registerObjectClickListener('A', 'markClick');
                    api.registerClientListener(event => {
                        if (event.type === 'rename' && event.target === 'P')
                            ggbApplet.setValue('b', 4);
                    });
                }
                """,
        });
        document.InitializeApplet();
        Assert.Equal(3, document.State.AppletListeners.Count);
        document.Execute("Rename(A,P)");
        document.RunAppletEvent("OnRename", "P", "A");
        document.RunAppletEvent("OnAdd", "P", "");
        document.RunAppletEvent("OnClick", "P", "");
        document.RunScript("js: ggbApplet.unregisterAddListener(markAdd); ggbApplet.setValue('a', 0);");
        document.RunAppletEvent("OnAdd", "P", "");
        var result = new ConstructionKernel().Evaluate(document.State);
        Assert.Equal(1, result.Values["n"].Number);
        Assert.Equal(0, result.Values["a"].Number);
        Assert.Equal(4, result.Values["b"].Number);
        Assert.Equal(new GeoVector(1, 2, 3), result.Values["P"].Origin);
        document.Execute("Delete(P)");
        Assert.Single(document.State.AppletListeners);
    }

    [Fact]
    public void AttachedPointsFollowTheirPathParameterAndCanBecomeFreePoints()
    {
        var document = new ConstructionDocument();
        document.Execute("A=(0,0);B=(4,0);s=Segment(A,B);P=(1,2)");
        var kernel = new ConstructionKernel();
        document.AttachPoint(kernel.Evaluate(document.State), "P", "s", inside: false);
        var attached = kernel.Evaluate(document.State).Values["P"];
        Assert.Equal(new GeoVector(1, 0), attached.Origin);
        Assert.Equal("s", ConstructionDocument.AttachedPath(document.State.Objects.Single(item => item.Label == "P"), attached));
        document.Update("P", item => item with { Definition = "PointOnObject(s,.75)" });
        var moved = kernel.Evaluate(document.State);
        Assert.Equal(3, moved.Values["P"].Origin.X);
        document.DetachPoint(moved, "P");
        Assert.True(ConstructionDocument.IsFreePoint(document.State.Objects.Single(item => item.Label == "P")));
        document.Execute("B=(8,0)");
        Assert.Equal(3, kernel.Evaluate(document.State).Values["P"].Origin.X);
        Assert.Throws<FormatException>(() => document.AttachPoint(kernel.Evaluate(document.State), "A", "s", inside: false));
    }

    [Fact]
    public void GeoGebraPointOverloadsDistinguishPathsFromCoordinatesAndVectorOffsets()
    {
        var document = new ConstructionDocument();
        document.Execute("a=2;b=3;A=Point(a,b);B=Point(A,Vector((3,4)));C=Point({2,3,4});s=Segment(A,B);P=Point(s);Q=Point(Line(A,B))");
        var result = new ConstructionKernel().Evaluate(document.State);
        Assert.Equal(new GeoVector(5, 7), result.Values["B"].Origin);
        Assert.Equal(new GeoVector(2, 3, 4), result.Values["C"].Origin);
        foreach (var item in document.State.Objects.Where(item => item.Label is "A" or "B" or "C" or "P"))
            Assert.Equal(item.Label == "P" ? "s" : null, ConstructionDocument.AttachedPath(item, result.Values[item.Label]));
        var inlinePoint = document.State.Objects.Single(item => item.Label == "Q");
        Assert.Equal("Line(A,B)", ConstructionDocument.AttachedPath(inlinePoint, result.Values["Q"]));
        Assert.Equal(GeometryKind.Line, result.Values["Q"].AttachmentTarget.Kind);
        document.DetachPoint(result, "Q");
        Assert.True(ConstructionDocument.IsFreePoint(document.State.Objects.Single(item => item.Label == "Q")));
    }

    [Fact]
    public void GeoGebraClientModeCanPositionAControlWithoutReplacingItsDefinition()
    {
        var document = new ConstructionDocument();
        document.Execute("widget=Button(\"Next\")");
        document.Load(document.State with
        {
            AppletScript = """
                function ggbOnInit() {
                    ggbApplet.registerClientListener(event => {
                        if (event.type === 'setMode' && event.argument === '67')
                            ggbApplet.setCoords('widget', 120, 75);
                    });
                }
                """,
        });
        document.InitializeApplet();
        document.RunAppletEvent("OnSetMode", "", "", "67");
        var widget = Assert.Single(document.State.Objects);
        Assert.Equal("Button(\"Next\")", widget.Definition);
        Assert.True(widget.ControlPositionSet);
        Assert.Equal(120, widget.ControlX);
        Assert.Equal(75, widget.ControlY);
    }

    [Fact]
    public void PointInPolygonClampsOutsideDragAndFollowsThePolygon()
    {
        var document = new ConstructionDocument();
        document.Execute("A=(0,0);B=(4,0);C=(0,4);g=Polygon(A,B,C);P=(1,1)");
        var kernel = new ConstructionKernel();
        var initial = kernel.Evaluate(document.State);
        Assert.True(GeometryPath.TryGetRegionDefinition("g", initial.Values["g"], new GeoVector(3, 3), out string definition));
        document.Update("P", item => item with { Definition = definition });
        var attached = kernel.Evaluate(document.State).Values["P"];
        Assert.Equal("g", ConstructionDocument.AttachedPath(document.State.Objects.Single(item => item.Label == "P"), attached));
        Assert.Equal(new GeoVector(2, 2), attached.Origin);
        document.Execute("B=(6,0);C=(0,6)");
        Assert.Equal(new GeoVector(3, 3), kernel.Evaluate(document.State).Values["P"].Origin);
    }

    [Fact]
    public void CommonTangentsForSeparatedCirclesAreDistinctAndTouchBothCircles()
    {
        var result = Evaluate("c=Circle((0,0),1);d=Circle((5,0),2);t=CommonTangents(c,d)");
        var tangents = result.Values["t"].Items;

        Assert.Equal(4, tangents.Count);
        foreach (var tangent in tangents)
        {
            double firstDistance = Math.Abs(tangent.Direction.X * tangent.Origin.Y - tangent.Direction.Y * tangent.Origin.X) / tangent.Direction.Length;
            var secondCenter = new GeoVector(5, 0);
            double secondDistance = Math.Abs(tangent.Direction.X * (tangent.Origin.Y - secondCenter.Y)
                - tangent.Direction.Y * (tangent.Origin.X - secondCenter.X)) / tangent.Direction.Length;
            Assert.Equal(1, firstDistance, 7);
            Assert.Equal(2, secondDistance, 7);
        }
    }

    [Theory]
    [InlineData("c=Circle((0,0),1);d=Ellipse((5,0),(7,0),2);t=CommonTangents(c,d)", 4)]
    [InlineData("g=Line((-1,-1),(1,-1));p=Parabola((0,1),g);q=Parabola((4,1),g);t=CommonTangents(p,q)", 1)]
    [InlineData("c=Conic((1,0),(0,1),(-1,0),(0,-1),(.6,.8));d=Circle((5,0),1);t=CommonTangents(c,d)", 4)]
    public void CommonTangentsEnumerateFiniteSolutionsForDifferentConicRepresentations(string definitions, int count)
    {
        var result = Evaluate(definitions);
        Assert.Equal(count, result.Values["t"].Items.Count);
    }

    [Fact]
    public void GeneralConicSupportsLineIntersectionsAndTangentsFromExteriorPoint()
    {
        var result = Evaluate("c=Conic((1,0),(0,1),(-1,0),(0,-1),(.6,.8));g=Line((-2,0),(2,0));"
            + "i=Intersect(g,c);t=Tangent((2,0),c)");
        Assert.Equal(2, result.Values["i"].Items.Count);
        Assert.Equal(-1, result.Values["i"].Items[0].Origin.X, 7);
        Assert.Equal(1, result.Values["i"].Items[1].Origin.X, 7);
        Assert.Equal(2, result.Values["t"].Items.Count);
    }

    [Theory]
    [InlineData("a=b+1;b=a+1")]
    [InlineData("a=a+1")]
    public void CyclesAreReportedOnObjects(string input)
    {
        var document = new ConstructionDocument();
        document.Execute(input);
        Assert.All(new ConstructionKernel().Evaluate(document.State).Values.Values, value => Assert.False(value.Defined));
    }

    [Theory]
    [InlineData("A=(0,0);B=(4,0);C=(0,3);g=Polygon(A,B,C);v=Area(g)", 6)]
    [InlineData("g=Cube((0,0,0),3);v=Volume(g)", 27)]
    [InlineData("g=Sphere((0,0,0),2);v=Volume(g)", 33.5103216382911)]
    [InlineData("f(x)=x^3;g=Derivative(f,x);v=g(2)", 12)]
    [InlineData("a=2;f(x)=a*x^2;v=f(3)", 18)]
    public void GeometryAndAlgebraCommandsShareDependencies(string input, double expected)
        => Assert.Equal(expected, Evaluate(input).Values["v"].Number, 7);

    [Theory]
    [InlineData("g=Surface(sin(x)*cos(y),-3,3,-3,3)")]
    [InlineData("g=Surface(cos(u)*sin(v),sin(u)*sin(v),cos(v),u,0,2*pi,v,0,pi)")]
    [InlineData("g=x^2+y^2+z^2=9")]
    [InlineData("g=x^2+y^2<=9 && y>=0")]
    [InlineData("g=Icosahedron((0,0,0),2)")]
    [InlineData("g=Dodecahedron((0,0,0),2)")]
    public void MeshedObjectsHaveFiniteFaces(string input)
    {
        var value = Evaluate(input).Values["g"];
        Assert.NotEmpty(value.Faces);
        Assert.All(value.Faces, face => Assert.True(face.A.Finite && face.B.Finite && face.C.Finite));
    }

    [Fact]
    public void ConicFeaturesAndSpatialSectionsAreConsistent()
    {
        var result = Evaluate("p=Line((-2,-2),(2,-2));c=Parabola((0,2),p);v=Vertex(c);d=Directrix(c);s=Cube((-1,-1,-1),2);q=Plane((0,0,0),Vector((0,0,1)));cut=Intersect(q,s)");
        Assert.Equal(default, result.Values["v"].Origin);
        Assert.Equal(-2, result.Values["d"].Origin.Y);
        Assert.NotEmpty(result.Values["cut"].Paths);
        Assert.All(result.Values["cut"].Paths.SelectMany(path => path), point => Assert.InRange(Math.Abs(point.Z), 0, 1e-8));
    }

    [Fact]
    public async Task SaveAndShapeMetadataRetainDependenciesAndView()
    {
        var document = new ConstructionDocument();
        document.Execute("a=2;A=(a,1);B=(5,4);s=Segment(A,B)");
        document.SetView(document.State with { ThreeDimensional = true, Yaw = 1.4 });
        var encoded = ConstructionExport.Encode(document.State);
        var restored = ConstructionExport.Decode(encoded);
        Assert.Equal(document.State.Objects, restored.Objects);
        Assert.True(restored.ThreeDimensional);
        Assert.Equal(1.4, restored.Yaw);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".icgeo");
        try
        {
            await ConstructionDocument.SaveAsync(path, restored);
            var opened = await ConstructionDocument.OpenAsync(path);
            Assert.Equal(2, new ConstructionKernel().Evaluate(opened).Values["A"].Origin.X);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void VectorExportsAreBoundedAndCancellationWorks()
    {
        var document = new ConstructionDocument();
        document.Execute("g=Line((-1,0),(1,0));s=Cube((0,0,0),2)");
        var result = new ConstructionKernel().Evaluate(document.State);
        Assert.Contains("<svg", ConstructionExport.Svg(document.State, result));
        Assert.Contains("facet normal", ConstructionExport.Stl(document.State, result));
        Assert.Contains("\\begin{tikzpicture}", ConstructionExport.Tikz(document.State, result));
        Assert.Contains("\\begin{pspicture}", ConstructionExport.Pstricks(document.State, result));
        Assert.Contains("\\psline", ConstructionExport.Pstricks(document.State, result));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new ConstructionKernel().Evaluate(document.State, cancellation.Token));
    }

    [Fact]
    public async Task AnimatedGifEncoderWritesLoopingGif89aFrames()
    {
        var frames = new List<byte[]>();
        for (int frame = 0; frame < 3; frame++)
        {
            var pixels = new byte[64 * 64 * 4];
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int pixelOffset = (y * 64 + x) * 4;
                pixels[pixelOffset] = (byte)((x * 3 + y * 7 + frame * 41) % 256);
                pixels[pixelOffset + 1] = (byte)((x * 11 + y * 5 + frame * 23) % 256);
                pixels[pixelOffset + 2] = (byte)((x * 13 + y * 17 + frame * 61) % 256);
                pixels[pixelOffset + 3] = 255;
            }
            frames.Add(pixels);
        }

        byte[] gif = AnimatedGifEncoder.Encode(64, 64, frames, TimeSpan.FromMilliseconds(100));

        Assert.True(gif.AsSpan().StartsWith("GIF89a"u8));
        Assert.Contains("NETSCAPE2.0", System.Text.Encoding.ASCII.GetString(gif));
        Assert.Equal(0x3B, gif[^1]);

        int gifOffset = 13 + 3 * 256;
        int frameCount = 0;
        while (gif[gifOffset] != 0x3B)
        {
            byte marker = gif[gifOffset++];
            if (marker == 0x21)
            {
                gifOffset++;
                int blockLength;
                while ((blockLength = gif[gifOffset++]) > 0) gifOffset += blockLength;
            }
            else
            {
                Assert.Equal(0x2C, marker);
                gifOffset += 9;
                gifOffset++;
                int blockLength;
                while ((blockLength = gif[gifOffset++]) > 0) gifOffset += blockLength;
                frameCount++;
            }
        }
        Assert.Equal(3, frameCount);

        using var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(gif);
        await writer.StoreAsync();
        writer.DetachStream();
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        Assert.Equal(3u, decoder.FrameCount);
        var bitmap = await (await decoder.GetFrameAsync(0)).GetSoftwareBitmapAsync();
        Assert.Equal(64, bitmap.PixelWidth);
        Assert.Equal(64, bitmap.PixelHeight);
    }

    [Theory]
    [InlineData("c=Circle((0,0),3);A=(5,0);g=Tangent(A,c)")]
    [InlineData("a=1;f(x)=a*sin(x);d=Derivative(f,x);R=Root(f);E=Extremum(f)")]
    [InlineData("g=PolarCurve(3*cos(5*t),0,2*pi)")]
    [InlineData("p=RegularPolygon((-2,-1),(2,-1),5);g=Prism(p,4);h=Pyramid(p,6)")]
    [InlineData("g=Sequence((k,sin(k)),k,-8,8,0.5)")]
    [InlineData("g=Zip(k^2,k,{1,2,3,4})")]
    [InlineData("f(x)=x^2/4;g=OsculatingCircle(f,2)")]
    [InlineData("g=SlopeField(x-y);h=SolveODE(x-y,0,1,6)")]
    [InlineData("g=Curve(2+t,0,t,0,4);a=Line((0,0,0),(0,0,1));h=Revolve(g,a)")]
    public void ConstructionExamplesHaveDefinedOutputs(string input) => Evaluate(input);

    [Theory]
    [InlineData("Cube")][InlineData("Tetrahedron")][InlineData("Octahedron")][InlineData("Dodecahedron")][InlineData("Icosahedron")]
    public void UnfoldingProducesCoplanarFaces(string solid)
    {
        var result = Evaluate($"g={solid}((0,0,0),2);n=Net(g,1)");
        var faces = result.Values["n"].Faces;
        Assert.NotEmpty(faces);
        var first = faces[0];
        var normal = (first.B - first.A).Cross(first.C - first.A).Unit();
        Assert.All(faces.SelectMany(face => new[] { face.A, face.B, face.C }),
            point => Assert.InRange(Math.Abs((point - first.A).Dot(normal)), 0, 1e-7));
    }

    [Fact]
    public void ConcavePolygonAreaUsesInteriorTriangulation()
    {
        var value = Evaluate("g=Polygon((0,0),(4,0),(4,4),(2,1),(0,4));v=Area(g)").Values["v"];
        Assert.Equal(10, value.Number, 8);
    }

    [Theory]
    [InlineData("AreParallel(l,m)")]
    [InlineData("AreCollinear(A,M,B)")]
    public void ExactProofsUseSymbolicConstructionIdentities(string predicate)
    {
        var result = Evaluate($"A=(-2,1);B=(3,4);M=Midpoint(A,B);l=Line(A,B);m=Parallel((0,1),l);proof=ProveDetails({predicate})");
        Assert.Equal(1, result.Values["proof"].Number);
        Assert.Contains("符号恒等式", result.Values["proof"].Display);
    }

    [Fact]
    public void ProofUsesExactCounterexampleForCoincidentallyCollinearFreePoints()
    {
        var result = Evaluate("A=(1,2); B=(3,4); C=(5,6); relation=AreCollinear(A,B,C); claim=Prove(relation)");
        Assert.Equal(1, result.Values["relation"].Number);
        Assert.Equal(GeometryKind.Boolean, result.Values["claim"].Kind);
        Assert.Equal(0, result.Values["claim"].Number);
        Assert.Contains("精确反例", result.Values["claim"].Display);
    }

    [Fact]
    public void FixingPointsRerunsSymbolicProofWithTheirCoordinates()
    {
        var document = new ConstructionDocument();
        document.Execute("A=(1,2); B=(3,4); C=(5,6); claim=Prove(AreCollinear(A,B,C))");
        var kernel = new ConstructionKernel();
        Assert.Equal(0, kernel.Evaluate(document.State).Values["claim"].Number);
        foreach (string label in new[] { "A", "B", "C" }) document.Update(label, item => item with { Fixed = true });
        Assert.Equal(1, kernel.Evaluate(document.State).Values["claim"].Number);
    }

    [Fact]
    public void ProofAcceptsSymbolicScalarEquality()
    {
        var result = Evaluate("a=3; b=a^2+2*a+1; claim=Prove(b==(a+1)^2)");
        Assert.Equal(GeometryKind.Boolean, result.Values["claim"].Kind);
        Assert.Equal(1, result.Values["claim"].Number);
    }

    [Fact]
    public void ProofUsesDefinitionsOfRotationsReflectionsAndGeometricMeasurements()
    {
        var result = Evaluate("A=(1,1);B=(2,3);C=(4,0);D=Rotate(B,90°,A);R=Reflect(B,C);M=Midpoint(B,R); " +
            "length=Distance(A,B); rotation=Prove(Distance(A,D)==length); reflection=Prove(AreEqual(M,C))");
        Assert.True(result.Values["rotation"].Number == 1, result.Values["rotation"].Display);
        Assert.True(result.Values["reflection"].Number == 1, result.Values["reflection"].Display);
    }

    [Fact]
    public void GeoGebraSpatialOverloadsPreserveEdgesAndDirections()
    {
        var result = Evaluate("A=(0,0,0); B=(2,0,0); C=(2,2,0); P=(1,2,3); base=Polygon(A,B,C,(0,2,0)); edge=Line(A,B); " +
            "cube1=Cube(base); cube2=Cube(A,B,C); cube3=Cube(A,B,xOyPlane); n=PerpendicularLine(P,xOyPlane); m=PerpendicularLine(P,edge,space)");
        var expected = result.Values["cube1"];
        Assert.Equal(8, expected.Number);
        Assert.Equal(expected.Points, result.Values["cube2"].Points);
        Assert.Equal(expected.Points, result.Values["cube3"].Points);
        Assert.Equal(new GeoVector(0, 0, 1), result.Values["n"].Direction);
        Assert.Equal(new GeoVector(0, -2, -3), result.Values["m"].Direction);
    }

    [Fact]
    public void LocusReevaluatesDependenciesWithoutChangingTheDocument()
    {
        var result = Evaluate("a=0;P=(a,a^2);g=Locus(P,a,-3,3)");
        Assert.Equal(0, result.Values["a"].Number);
        var points = result.Values["g"].Paths.SelectMany(path => path).ToArray();
        Assert.True(points.Length > 90);
        Assert.All(points, point => Assert.Equal(point.X * point.X, point.Y, 8));
    }

    [Theory]
    [InlineData("Solve(x^2=2,x)")]
    [InlineData("Solve({x+y=5,x-y=1},{x,y})")]
    [InlineData("Inverse([[1,2],[3,4]])")]
    [InlineData("Expand((2+3*i)^3)")]
    [InlineData("Limit(sin(x)/x,x,0)")]
    public void CasSupportsEquationsMatricesComplexNumbersAndLimits(string input)
        => Assert.False(string.IsNullOrWhiteSpace(ComputerAlgebra.Calculate(input).Exact));

    [Theory]
    [InlineData("g=If(x<0,-x,x^2)")]
    [InlineData("f(x)=x^2;g(x)=f(sin(x))")]
    [InlineData("f(x,y)=sin(x)*cos(y)")]
    [InlineData("A=(0,0);B=(4,0);r=Distance(A,B)/2;c=Circle(A,r)")]
    [InlineData("DefineTool(DiameterCircle,{U,V},Circle(Midpoint(U,V),Distance(U,V)/2));A=(-3,0);B=(3,2);c=DiameterCircle(A,B)")]
    [InlineData("a=0;L=Line((a,0),(0,4-a));g=Envelope(L,a,1,3)")]
    public void ComposedFunctionsToolsAndMeasurementsAreDefined(string input) => Evaluate(input);

    [Fact]
    public void EventScriptsAreAtomicAndUseCurrentValues()
    {
        var document = new ConstructionDocument();
        document.Execute("a=2;P=(a,1);g=Circle(P,2)");
        document.RunScript("SetValue(a,a+1);SetVisible(g,false);SetCoords(P,4,5)");
        var result = new ConstructionKernel().Evaluate(document.State);
        Assert.Equal(3, result.Values["a"].Number);
        Assert.Equal(new GeoVector(4,5), result.Values["P"].Origin);
        Assert.False(document.State.Objects.Single(item => item.Label == "g").Visible);
        var before = document.State;
        Assert.Throws<FormatException>(() => document.RunScript("SetValue(a,9);SetValue(missing,1)"));
        Assert.Same(before, document.State);
        document.Undo();
        Assert.Equal(2, new ConstructionKernel().Evaluate(document.State).Values["a"].Number);
    }

    [Fact]
    public void ProjectionPreservesCirclesAndSpatialDraggingAtDifferentAspectRatios()
    {
        var projection = new GeometryProjection(new(),1600,600);
        var center = projection.Project(default);
        Assert.Equal(VectorDistance(center, projection.Project(new(1,0))), VectorDistance(center, projection.Project(new(0,1))), 5);
        foreach (bool perspective in new[] { true,false })
        {
            var spatial = new GeometryProjection(new() { ThreeDimensional = true, Perspective = perspective },1600,600);
            var original = new GeoVector(2,3,1);
            var restored = spatial.Unproject(spatial.Project(original),original.Z);
            Assert.Equal(original.X, restored.X,5); Assert.Equal(original.Y, restored.Y,5);
        }
        static double VectorDistance(System.Numerics.Vector2 a, System.Numerics.Vector2 b) => System.Numerics.Vector2.Distance(a,b);
    }

    [Theory]
    [InlineData("g=Conic((3,0),(0,2),(-3,0),(0,-2),(1.8,1.6))")]
    [InlineData("A=(1,2);B=(4,3);g=RigidPolygon({(0,0),(3,0),(1,2)},A,B)")]
    [InlineData("A=(1,1);u=Vector((3,0));v=Vector((-1,2));g=VectorPolygon(A,{u,v})")]
    [InlineData("s=Sphere((0,0,0),3);c=Cylinder((0,0,-4),(0,0,4),2);g=Intersect(s,c)")]
    [InlineData("g=x+y+z=0")]
    [InlineData("g=ComplexPoint((2+3*i)^2);z1=Complex(g)")]
    public void ExtendedConstructionsAreDefined(string input)
    {
        var value = Evaluate(input).Values["g"];
        Assert.True(value.Paths.Count + value.Faces.Count + value.Points.Count > 0);
    }

    [Fact]
    public void CasResolvesExactValuesFromTheConstruction()
    {
        var document = new ConstructionDocument(); document.Execute("a=1/3;f(x)=a*x^2");
        var evaluation = new ConstructionKernel().Evaluate(document.State);
        string resolved = ConstructionKernel.ResolveExpression(document.State, evaluation, "Integral(f,x,0,3)");
        Assert.Equal("3",ComputerAlgebra.Calculate(resolved).Exact);
    }

    [Fact]
    public void DoubleRootsAndStationaryInflectionsAreClassifiedCorrectly()
    {
        var result = Evaluate("f(x)=(x-0.123)^2;r=Root(f);g(x)=x^3;e=Extremum(g);i=InflectionPoint(g)");
        Assert.Single(result.Values["r"].Items);
        Assert.Equal(.123,result.Values["r"].Items[0].Origin.X,5);
        Assert.Empty(result.Values["e"].Items);
        Assert.Single(result.Values["i"].Items);
    }

    [Fact]
    public void JavaScriptUpdatesAreTransactionalAndBounded()
    {
        var document = new ConstructionDocument(); document.Execute("a=2;P=(1,2)");
        document.RunScript("js: ggbApplet.setValue('a',ggbApplet.getValue('a')+3); for(let k=0;k<4;k++) ggbApplet.evalCommand('Q'+k+'=('+k+','+(k*k)+')');");
        Assert.Equal(5,new ConstructionKernel().Evaluate(document.State).Values["a"].Number);
        Assert.Equal(6,document.State.Objects.Count);
        var before = document.State;
        Assert.ThrowsAny<Exception>(() => document.RunScript("js: ggbApplet.setValue('a',99); while(true){}"));
        Assert.Same(before,document.State);
        document.Undo();
        Assert.Equal(2,document.State.Objects.Count);
    }

    [Fact]
    public void NativeControlDefinitionsExposeValuesAndDependencies()
    {
        var result = Evaluate("a=2;show=Checkbox(true);input=InputBox(a);choice=Dropdown({1,2,3},2);next=Button(\"下一步\");g=If(show,Circle((0,0),a))");
        Assert.Equal(2,result.Values["choice"].Number);
        Assert.Equal("2",result.Values["input"].Display);
        Assert.Equal("button",result.Values["next"].ControlKind);
        Assert.NotEmpty(result.Values["g"].Paths);
    }

    [Fact]
    public void ObjectPointCoordinatesAndCurveMeasurementsRemainConsistent()
    {
        var result = Evaluate("a=2;P=Point(a,3);Q=Point(Circle((0,0),2),0);g=Sector((0,0),(2,0),(0,2));area=Area(g);c=CircumArc((1,0),(0,-1),(-1,0));f(x)=x^2;l=Length(f,0,1)");
        Assert.Equal(new GeoVector(2,3),result.Values["P"].Origin);
        Assert.Equal(new GeoVector(2,0),result.Values["Q"].Origin);
        Assert.Equal(Math.PI,result.Values["area"].Number,7);
        Assert.All(result.Values["c"].Paths.SelectMany(path => path), point => Assert.True(point.Y < 1e-8));
        Assert.InRange(result.Values["l"].Number,1.478,1.480);
    }

    [Fact]
    public void CircleInversionPreservesAnalyticCirclesAndLines()
    {
        var result = Evaluate("c=Circle((0,0),3);l=Line((2,0),(2,1));g=Reflect(l,c);h=Reflect(g,c);p=Reflect((4,2),c)");
        Assert.Equal(GeometryKind.Circle,result.Values["g"].Kind);
        Assert.Equal(2.25,result.Values["g"].Radius,8);
        Assert.Equal(GeometryKind.Line,result.Values["h"].Kind);
        Assert.Equal(2,result.Values["h"].Origin.X,8);
        Assert.Equal(new GeoVector(1.8,.9),result.Values["p"].Origin);
    }

    [Fact]
    public void LocalParametersAndQuotedTextHaveIndependentScopes()
    {
        var result = Evaluate("t=99;u=4;v=7;a=Button(\"a\");g=Curve(cos(t),sin(t),t,0,2*pi);h=Surface(u,v,u*v,u,-1,1,v,-1,1);f(t)=t^2;d=Derivative(f,t)");
        var curve = result.Values["g"].Paths.SelectMany(path => path).ToArray();
        Assert.All(curve,point => Assert.Equal(1,point.Length,7));
        Assert.Contains(result.Values["h"].Faces,face => face.A.X < 0);
        Assert.Contains(result.Values["f"].Paths.SelectMany(path => path),point => point.X < 0 && point.Y < 10);
        Assert.Equal("a",result.Values["a"].Display);
        Assert.Equal(GeometryKind.Function,result.Values["d"].Kind);
        Assert.Equal("t",result.Values["d"].Variable);
    }
}
