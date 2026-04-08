using Microsoft.CodeAnalysis.CSharp.Syntax;
using Plugin;
using System.Linq;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;


namespace SqlcGenCsharp.Drivers.Generators;

public class ManyDeclareGen(DbDriver dbDriver)
{
    private CommonGen CommonGen { get; } = new(dbDriver);

    public MemberDeclarationSyntax Generate(string queryTextConstant, string argInterface, string returnInterface, Query query)
    {
        var parametersStr = CommonGen.GetMethodParameterList(argInterface, query.Params);
        var returnType = $"Task<List<{returnInterface}>>";
        return ParseMemberDeclaration($$"""
            public async {{returnType}} {{query.Name.ToMethodName(dbDriver.Options.WithAsyncSuffix)}}({{parametersStr}})
            {
                {{GetMethodBody(queryTextConstant, returnInterface, query)}}
            }
            """)!;
    }

    private string GetMethodBody(string queryTextConstant, string returnInterface, Query query)
    {
        var sqlTextTransform = CommonGen.GetSqlTransformations(query, queryTextConstant);
        var anyEmbeddedTableExists = query.Columns.Any(c => c.EmbedTable is not null);
        var useDapper = dbDriver.Options.UseDapper && !anyEmbeddedTableExists;

        var dapperParams = useDapper ? CommonGen.ConstructDapperParamsDict(query) : string.Empty;
        var sqlVar = sqlTextTransform != string.Empty ? Variable.TransformedSql.AsVarName() : queryTextConstant;
        var transactionProperty = Variable.Transaction.AsPropertyName();

        var noTxBody = useDapper ? GetDapperNoTxBody(sqlVar, returnInterface, query) : GetDriverNoTxBody(sqlVar, returnInterface, query);

        return $$"""
            {{sqlTextTransform}}
            {{dapperParams}}
            {{noTxBody}}
        """;
    }

    private string GetDapperNoTxBody(string sqlVar, string returnInterface, Query query)
    {
        var dapperArgs = CommonGen.GetDapperArgs(query);
        var returnType = dbDriver.AddNullableSuffixIfNeeded(returnInterface, true);
        var resultVar = Variable.Result.AsVarName();
        return
            $"""
             var {resultVar} = await {Variable.Connection.AsFieldName()}.QueryAsync<{returnType}>({sqlVar}{dapperArgs});
             return {resultVar}.AsList();
             """;
    }

    private string GetDriverNoTxBody(string sqlVar, string returnInterface, Query query)
    {
        var dataclassInit = CommonGen.InstantiateDataclass([.. query.Columns], returnInterface, query);
        var resultVar = Variable.Result.AsVarName();
        var readWhileExists = $$"""
            while ({{CommonGen.AwaitReaderRow()}})
                {{resultVar}}.Add({{dataclassInit}});
        """;
        var sqlCommands = dbDriver.CreateSqlCommand(sqlVar);
        var commandBlock = sqlCommands.CommandCreation.WrapBlock(
            $$"""
            {{sqlCommands.SetCommandText.AppendSemicolonUnlessEmpty()}}
            {{dbDriver.AddParametersToCommand(query)}}
            {{sqlCommands.PrepareCommand.AppendSemicolonUnlessEmpty()}}
            using ({{CommonGen.InitDataReader()}})
            {
                var {{resultVar}} = new List<{{returnInterface}}>();
                {{readWhileExists}}
                return {{resultVar}};
            }
            """
        );
        return
            $$"""
              {{commandBlock}}
              """;
    }
}