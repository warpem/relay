window.exampleLogin = {
    loginViaFetch: async function (loginData) {
        // Prepare form data
        const formData = new FormData();
        formData.append("Username", loginData.username);
        formData.append("Password", loginData.password);
        formData.append("RememberMe", loginData.rememberMe);

        // Perform a POST to /process-login, including credentials
        // so the server’s Set-Cookie is accepted by the browser
        const response = await fetch("/process-login", {
            method: "POST",
            body: formData,
            credentials: "include" // ensures cookies flow
        });

        if (!response.ok) {
            // e.g. 401 or 500
            return false;
        }

        return true;
    }
};