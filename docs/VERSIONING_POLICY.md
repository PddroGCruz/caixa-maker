# Política Permanente de Versionamento — Caixa Maker

O **Caixa Maker** utiliza **Semantic Versioning (`MAJOR.MINOR.PATCH`)** com fonte única de verdade centralizada no arquivo `VERSION` na raiz do repositório.

---

## 1. Critérios de Incremento (`MAJOR.MINOR.PATCH`)

| Tipo de entrega | Segmento incrementado | Exemplos |
| :--- | :--- | :--- |
| **Correção de bug / ajuste sem quebra funcional** | **`PATCH`** (`X.Y.(Z+1)`) | Correção da tecla `Delete`, ajuste de exportador DXF, correção visual |
| **Nova funcionalidade compatível ou melhoria funcional relevante** | **`MINOR`** (`X.(Y+1).0`) | Novo modelo de caixa no catálogo, nova ferramenta no editor 2D |
| **Mudança incompatível ou quebra estrutural / contrato** | **`MAJOR`** (`(X+1).0.0`) | Alteração incompatível de formato de projeto ou API pública |
| **Refatoração puramente interna sem impacto funcional** | Avaliar antes de distribuir | Não incrementar sem necessidade de nova release |

Regras estritas:
- Nunca pular versões nem reutilizar o mesmo número para distribuições distintas.
- Nunca incrementar a versão repetidamente dentro de uma mesma tarefa lógica (uma entrega lógica = um incremento).
- **Regra obrigatória de manutenção:** nenhuma tarefa que altere comportamento, correção de erro ou funcionalidade do aplicativo pode ser encerrada sem avaliar e aplicar o incremento correspondente em `VERSION` + `CHANGELOG.md`.

---

## 2. Arquitetura de Centralização e Propagação Automática

A versão reside em **`VERSION`** e propaga automaticamente para toda a pilha:

1. **Frontend Web/Desktop (`React + Vite`):** `frontend/vite.config.ts` lê `../VERSION` em tempo de build e testes (`Vitest`) e injeta `__APP_VERSION__` em `frontend/src/services/api.ts` (`APP_VERSION`), refletindo automaticamente no Cabeçalho, Rodapé, Tela Inicial, Tela de Startup Desktop e Modal **Sobre**.
2. **Backend Python (`FastAPI` + Sidecar PyInstaller):** `backend/app_version.py` lê `VERSION` (embutido no sidecar via `scripts/caixa-maker-backend.spec`) e expõe a versão no aplicativo FastAPI e em `GET /api/health`.
3. **Aplicação Desktop Tauri, PE Windows e Instaladores (NSIS/MSI):** `scripts/version_manager.py` mantém sincronizados `frontend/package.json`, `frontend/package-lock.json`, `frontend/src-tauri/tauri.conf.json`, `frontend/src-tauri/Cargo.toml` e `frontend/src-tauri/Cargo.lock`, dos quais o Tauri gera automaticamente os metadados do executável Windows (`FileVersion` / `ProductVersion`) e dos instaladores.
4. **Pipeline de Release (`scripts/build_desktop_release.ps1`):** valida `scripts/version_manager.py --check` antes da compilação, bloqueando qualquer build com versões dessincronizadas, e nomeia automaticamente os artefatos finais em `release/`.

---

## 3. Procedimento Padrão em Cada Futura Tarefa

1. Consultar a versão atual:
   ```powershell
   .\.venv\Scripts\python.exe scripts\version_manager.py --current
   ```
2. Classificar a natureza da alteração (`patch`, `minor` ou `major`).
3. Aplicar o incremento único e sincronizar todos os manifestos:
   ```powershell
   .\.venv\Scripts\python.exe scripts\version_manager.py --bump patch   # ou minor / major
   ```
   *(Ou usar diretamente `-Bump patch|minor|major` em `scripts\build_desktop_release.ps1`.)*
4. Registrar a nova seção `## [X.Y.Z] - YYYY-MM-DD` no topo de [CHANGELOG.md](../CHANGELOG.md).
5. Verificar consistência e rodar suítes + build antes de encerrar:
   ```powershell
   .\.venv\Scripts\python.exe scripts\version_manager.py --check
   powershell -ExecutionPolicy Bypass -File scripts\build_desktop_release.ps1
   ```
