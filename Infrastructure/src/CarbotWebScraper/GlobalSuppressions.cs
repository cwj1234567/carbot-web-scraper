[assembly:
    System.Diagnostics.CodeAnalysis.SuppressMessage("Potential Code Quality Issues",
        "RECS0026:Possible unassigned object created by 'new'",
        Justification = "Constructs add themselves to the scope in which they are created")]
