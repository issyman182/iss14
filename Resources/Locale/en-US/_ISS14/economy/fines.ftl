# iss14: Economy — fines, legal terminal, mandates
fine-terminal-name = Legal terminal
fine-terminal-window-title = Legal Terminal
fine-terminal-slot-id = ID card
fine-terminal-slot-mandate = Agreement
fine-terminal-tab-own = My fines
fine-terminal-tab-lawyer = Attorney
fine-terminal-tab-security = Security

fine-terminal-own-hint = Insert your ID card to see and pay your fines. Paid fines clear a Wanted status automatically.
fine-terminal-no-card-inserted = No ID card inserted.
fine-terminal-card = { $name } — open fines: { $total } credits
fine-terminal-no-fines = No fines on record.
fine-terminal-pay = Pay
fine-terminal-fine-open = { $amount } cr — { $reason } (issued by { $issuer } at { $time })
fine-terminal-fine-paid = Paid: { $amount } cr — { $reason } (by { $by })

fine-terminal-lawyer-hint = Insert your attorney ID and a representation agreement signed by you and your client, then file it. Afterwards you can settle your client's fines from their account.
fine-terminal-no-mandate-paper = No agreement in the slot.
fine-terminal-mandate-incomplete = The agreement is missing a signature.
fine-terminal-mandate-ready = Agreement ready to file: { $client } represented by { $lawyer }.
fine-terminal-mandate-filed = Agreement on file: { $client } represented by { $lawyer }.
fine-terminal-file-mandate = File agreement
fine-terminal-not-lawyer-card = Insert an ID card with attorney access.
fine-terminal-no-client = No client on file. File a signed agreement first.
fine-terminal-client = Client: { $name } — attorney fee { $fee }% of each fine, paid by the client
fine-terminal-pay-client = Pay for client
fine-terminal-no-mandate = No agreement in the slot.
fine-terminal-mandate-lawyer-card = The attorney named on the agreement must insert their own ID card to file it.
fine-terminal-mandate-filed-ok = Agreement filed. You now represent { $client }.
fine-terminal-no-mandate-filed = You have not filed a representation agreement.
fine-terminal-not-lawyer = This card has no attorney access.
fine-terminal-client-insufficient = Your client cannot cover this: { $needed } credits needed including your fee.
fine-terminal-paid-client = Fine of { $amount } credits paid from the client's account. Your fee: { $fee } credits.

fine-terminal-search = Search records...
fine-terminal-no-selection = Select a record to issue or waive fines.
fine-terminal-selected = { $name }
fine-terminal-record-open = { $name } ({ $total } cr open)
fine-terminal-issue-heading = Issue fine
fine-terminal-amount = Amount
fine-terminal-reason = Reason
fine-terminal-issue = Issue
fine-terminal-waive = Waive
fine-terminal-no-access = Only Security may issue fines.
fine-terminal-invalid = Enter an amount and a reason.
fine-terminal-no-record = No record found.
fine-terminal-issued = Fined { $name } { $amount } credits.
fine-terminal-waived = Fine waived.
fine-terminal-no-card = Insert your ID card first.
fine-terminal-no-fine = That fine no longer exists.
fine-terminal-paid = Fine of { $amount } credits paid.

fine-history-issued = Fined { $amount } credits: { $reason }
fine-history-paid = Fine of { $amount } credits paid: { $reason }
fine-history-waived = Fine of { $amount } credits waived: { $reason }
fine-paid-by-salary = salary deduction
fine-paid-by-lawyer = attorney { $lawyer }
fine-salary-deducted = Fine of { $amount } credits ({ $reason }) was deducted from your salary.
fine-salary-deducted-partial = { $amount } credits of your fine ({ $reason }) were deducted from your salary; { $remaining } credits remain open.

legal-mandate-verb-sign-client = Sign as client
legal-mandate-verb-sign-lawyer = Sign as attorney
legal-mandate-same-person = You cannot represent yourself.
legal-mandate-signed-client = You sign the agreement as the client.
legal-mandate-signed-lawyer = You sign the agreement as the attorney.
legal-mandate-stamp-client = Client: { $name }
legal-mandate-stamp-lawyer = Attorney: { $name }
legal-mandate-unsigned = unsigned
legal-mandate-examine = Client: { $client }. Attorney: { $lawyer }. { $filed ->
    [true] Filed at a legal terminal.
   *[false] Not filed yet.
}

paper-legal-mandate-block-writing = The form is pre-printed. Use the signature lines instead.
paper-legal-mandate-content =
    { "" }[head=2]LEGAL REPRESENTATION AGREEMENT[/head]
    { "" }[italic]NanoTrasen standard form NT-LR-7[/italic]

    By signing this agreement as the [bold]client[/bold], you authorize the [bold]attorney[/bold] named below to represent you in all legal matters aboard this station, including:

    1. Representing you before Security and the Captain.
    2. Negotiating charges, sentences and fines on your behalf.
    3. [bold]Settling fines issued against you from your own bank account[/bold], plus the attorney's fee set by station regulations.

    This agreement takes effect once both parties have signed it and the attorney has filed it at a legal terminal. It ends with the shift.

    { "" }[italic]Sign with the "Sign as client" / "Sign as attorney" actions. Signatures are recorded electronically.[/italic]
