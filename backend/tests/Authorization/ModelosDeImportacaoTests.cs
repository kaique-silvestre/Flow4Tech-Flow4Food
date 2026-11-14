using Flow4Tech.Permissoes.Application;

namespace Flow4Tech.Authorization.Tests;

/// <summary>F4-0069: importing the menu is a manager decision; the waiter and the cashier models do not hold it (the owner is the factory profile, covered by <see cref="PoliticaDeFabricaTests"/>).</summary>
public sealed class ModelosDeImportacaoTests
{
    [Theory]
    [InlineData("gerente", true)]
    [InlineData("caixa", false)]
    [InlineData("garcom", false)]
    public void Only_the_manager_model_holds_the_menu_import_permission(string chave, bool tem) =>
        Assert.Equal(tem, ModelosDePerfil.Todos.Single(m => m.Chave == chave).Permissoes.Contains("cardapio:importar"));
}
