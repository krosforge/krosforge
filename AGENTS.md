# Instruções para agentes

## Commits, PRs e notas da versão

- Escreva em português as mensagens de commit, os títulos e as descrições dos PRs e as notas da versão.
- Use o formato Conventional Commits. Mantenha o tipo convencional e escreva o resumo em português, como em `fix: restaura a prévia do mapa de combate`.
- Todo PR deve criar ou atualizar `release-notes/<versão>.md` para a versão de destino. Acrescente as mudanças relevantes e mantenha as notas das demais entregas da mesma versão.
- No PR, indique o arquivo de notas da versão e registre a validação realizada. Preencha o [modelo de PR](.github/pull_request_template.md).
- Inclua `Co-Authored-By: Codex <noreply@openai.com>` nos commits produzidos pelo Codex.

## Desenvolvimento

- Use a versão do SDK .NET em `global.json`. As versões dos pacotes NuGet ficam em `Directory.Packages.props`.
- Consulte o [guia de contribuição](CONTRIBUTING.md) para testes, empacotamento e cuidados com a instalação do jogo.
