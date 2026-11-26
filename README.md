# Espaço em Disco

Um app pequeno para Windows, no estilo do TreeSize, para ver o que está ocupando espaço nos discos e apagar dali mesmo.

- Árvore de pastas ordenada por tamanho, com uma barra que mostra quanto cada pasta pesa na pasta-mãe
- Lista da pasta selecionada com tamanho, %, número de arquivos e de pastas e data de modificação
- **Del** manda para a Lixeira e **Shift+Del** exclui permanente, sempre com confirmação
- Não segue junctions nem links simbólicos: não conta nada em dobro e, ao apagar, remove só o link
- Funciona com caminhos de mais de 260 caracteres
- Botão para rodar como administrador e ver as pastas do sistema

## Baixar

Pegue o `EspacoDisco.exe` na página de [Releases](../../releases). Não precisa instalar: ele usa o .NET Framework 4.8, que já vem no Windows 10 e 11.

## Compilar

Rode `compilar.bat`. Ele usa o compilador C# que já vem no Windows (`csc.exe` do .NET Framework) e gera o `EspacoDisco.exe` na mesma pasta.

## Atalhos

| Tecla | O que faz |
|---|---|
| Del | Manda para a Lixeira |
| Shift+Del | Exclui permanente |
| F5 | Lê de novo a pasta selecionada |
| Enter / dois cliques | Entra na pasta ou abre o arquivo |
| Backspace | Volta para a pasta de cima |
