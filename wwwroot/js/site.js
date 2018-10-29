﻿//var fileList = [];
var isCtrlDown = false;

function searchFileList(){
    let searchParam = document.getElementById("searchBox").value;
    let fileList = document.getElementById("file-list").children;

    if(searchParam != ""){
        for(let i = 0; i < fileList.length; i+=2){
            console.log(fileList[i].children[0]);
            if (!fileList[i].children[0].textContent.toLowerCase().includes(searchParam.toLowerCase())) {
                fileList[i].setAttribute("hidden", true);
                fileList[i].nextElementSibling.setAttribute("hidden", true);
            }else if(fileList[i].hasAttribute("hidden")){
                fileList[i].removeAttribute("hidden");
                fileList[i].nextElementSibling.removeAttribute("hidden");
            }
        }
    }else{
        resetFileList();
    }
}


function resetFileList(){
    var fileList = document.getElementById("file-list").children;
    for(let i = 0; i < fileList.length; i++){
        fileList[i].removeAttribute("hidden", false);  
    }
    document.getElementById("resetButton").setAttribute("disabled",true);
    document.getElementById("searchButton").removeAttribute("disabled");
}

function showDownloadBox(){
    document.getElementById("download-panel").removeAttribute("hidden");
}

function downloadResource(downloadPath) { 
    showDownloadBox();
    var w = window.open(downloadPath, '_blank', '', true);  
    console.log(w);
    w.onclose = function(){
        document.getElementById("download-panel").setAttribute("hidden",true);
    }
    //document.getElementById("download-panel").setAttribute("hidden", true);
}

function copyToClipboard(id) {
    let el = document.getElementById(id);
    el.select();
    document.execCommand('copy');
}

function onPathSpanOut() {
    let promptLbl = document.getElementById("pathOutput");
    promptLbl.setAttribute("hidden", true);
}

function scrollLogAreaToEnd() {
    var textarea = document.getElementById('log-area');
    textarea.scrollTop = textarea.scrollHeight;
}

function changeVisibleTab(controlName) {
    let contentId = controlName.textContent.toLowerCase().replace(" ", "-");
    //controlName.getAttribute("class").concat(" active");
    let targetDiv = document.getElementById("container");
    if (targetDiv.children.length > 0) {
        for (let childIndex = 0; childIndex < targetDiv.children.length; childIndex++) {
            if (targetDiv.children[childIndex].getAttribute("id") != contentId) {
                targetDiv.children[childIndex].setAttribute("hidden", true);
            } else {
                targetDiv.children[childIndex].removeAttribute("hidden");
            }
        }
    }
    if (contentId == "logs") {
        scrollLogAreaToEnd();
    }
}

