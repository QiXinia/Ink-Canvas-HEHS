using InkCanvas.AnalyticGeometry.Algebra;
using InkCanvas.AnalyticGeometry.Construction;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ConstructionTests
{
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
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new ConstructionKernel().Evaluate(document.State, cancellation.Token));
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
