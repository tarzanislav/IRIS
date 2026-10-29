using Iris.Model;
using Iris.Tokenization;

// iris – the command line of the IRIS translator (more commands come with the translation core)
return args switch
{
    ["tokenize", var model, .. var words] when words.Length > 0 => Tokenize(model, string.Join(' ', words)),
    ["info", var model] => Info(model),
    _ => Usage(),
};

static int Tokenize(string modelPath, string text)
{
    var tokenizer = new SentencePieceTokenizer(SentencePieceModel.Load(modelPath));
    var pieces = tokenizer.Encode(text);
    Console.WriteLine(string.Join(' ', pieces.Select(p => p.Text)));
    Console.WriteLine(string.Join(' ', pieces.Select(p => p.Id)));
    return 0;
}

static int Info(string path)
{
    var model = ModelFile.Load(path);
    Console.WriteLine($"kind: {model.Kind}");
    Console.WriteLine($"config: {model.Config.ToJsonString()}");
    Console.WriteLine($"tensors: {model.Tensors.Count}, parameters: {model.Tensors.Sum(t => t.Count):N0}");
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("""
        iris tokenize <model.spm> <text>   pieces and ids of a SentencePiece model
        iris info <model.iris>             what an IRIS model file holds
        """);
    return 2;
}
